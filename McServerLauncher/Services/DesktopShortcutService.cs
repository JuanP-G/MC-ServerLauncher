using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Puts a shortcut to the app on the user's desktop.
/// <para>
/// Each desktop means something different by "shortcut": Windows wants a <c>.lnk</c>, Linux a
/// <c>.desktop</c> entry that has to be executable and, on GNOME, explicitly trusted, and macOS a
/// symlink to the bundle. What they share is that the target must be whatever this copy is really
/// running from — an AppImage the user dropped in Downloads, a bundle in /Applications — and not a
/// guessed install path.
/// </para>
/// </summary>
public static class DesktopShortcutService
{
    /// <summary>Where this copy of the app should be launched from.</summary>
    /// <remarks>
    /// For an AppImage that is the .AppImage file itself, not the executable inside its mount:
    /// the mount point disappears when the app closes, so a shortcut to it would break instantly.
    /// </remarks>
    public static string? LaunchTarget
    {
        get
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage)) return appImage;

            if (OperatingSystem.IsMacOS())
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (var i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
                    if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                        return dir.FullName;
            }

            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrEmpty(exe) ? null : exe;
        }
    }

    private static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>True when a desktop folder exists to put anything in.</summary>
    public static bool IsAvailable => LaunchTarget is not null && Directory.Exists(DesktopDir);

    /// <summary>
    /// Creates (or refreshes) the shortcut. Returns the path it wrote.
    /// </summary>
    /// <exception cref="InvalidOperationException">With a message meant for the user.</exception>
    public static string Create()
    {
        var target = LaunchTarget
            ?? throw new InvalidOperationException(Localizer.Get("Msg_ShortcutNoTarget"));

        var desktop = DesktopDir;
        if (!Directory.Exists(desktop))
            throw new InvalidOperationException(Localizer.Get("Msg_ShortcutNoDesktop"));

        if (OperatingSystem.IsWindows()) return CreateWindows(target, desktop);
        if (OperatingSystem.IsMacOS()) return CreateMacOs(target, desktop);
        if (OperatingSystem.IsLinux()) return CreateLinux(target, desktop);
        throw new InvalidOperationException(Localizer.Get("Msg_ShortcutFailed"));
    }

    // --- Windows -------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static string CreateWindows(string target, string desktop)
    {
        var link = Path.Combine(desktop, "MC Server Launcher.lnk");

        // A .lnk is a COM-built binary format. Rather than take a dependency on the Windows Script
        // Host interop assembly just for this, ask PowerShell — always present — to build it.
        // The paths travel as environment variables, never inside the script text: with -Command,
        // anything passed after it is appended to the command line and a path with spaces is then
        // parsed as more code, which is exactly how the first version of this failed.
        var script =
            "$s=(New-Object -ComObject WScript.Shell).CreateShortcut($env:MCSL_LINK);" +
            "$s.TargetPath=$env:MCSL_TARGET;" +
            "$s.WorkingDirectory=Split-Path $env:MCSL_TARGET;" +
            "$s.IconLocation=$env:MCSL_TARGET;" +
            "$s.Description='MC Server Launcher';" +
            "$s.Save()";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        psi.Environment["MCSL_LINK"] = link;
        psi.Environment["MCSL_TARGET"] = target;

        RunOrThrow(psi);
        if (!File.Exists(link)) throw new InvalidOperationException(Localizer.Get("Msg_ShortcutFailed"));
        return link;
    }

    // --- Linux ---------------------------------------------------------------------------------

    [SupportedOSPlatform("linux")]
    private static string CreateLinux(string target, string desktop)
    {
        var entry = Path.Combine(desktop, "mc-server-launcher.desktop");
        var icon = InstallLinuxIcon() ?? "applications-games";

        File.WriteAllText(entry, string.Join('\n', new[]
        {
            "[Desktop Entry]",
            "Type=Application",
            "Name=MC Server Launcher",
            "Comment=Manage your Minecraft servers",
            "Exec=" + Quote(target),
            "Icon=" + icon,
            "Terminal=false",
            "Categories=Game;Utility;",
            ""
        }));

        // A .desktop file that isn't executable shows up as a text file, and GNOME additionally
        // refuses to launch it until it is marked trusted — both are why "it did nothing" is the
        // usual outcome of writing one of these by hand.
        File.SetUnixFileMode(entry,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        TryTrust(entry);

        // Also register it in the applications menu, so it can be searched for as well as clicked.
        try
        {
            var apps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "applications");
            Directory.CreateDirectory(apps);
            File.Copy(entry, Path.Combine(apps, "mc-server-launcher.desktop"), overwrite: true);
        }
        catch { /* the desktop icon is what was asked for; the menu entry is a bonus */ }

        return entry;
    }

    /// <summary>
    /// Copies the app icon somewhere permanent and returns that path, or null if there is no icon.
    /// </summary>
    /// <remarks>
    /// The icon ships inside the AppImage, and $APPDIR is its <em>mount point</em> — a
    /// /tmp/.mount_XXXX directory that only exists while the app runs. Pointing Icon= there gives a
    /// shortcut whose icon works until you close the app and is a dangling path forever after, which
    /// is what the desktop then draws as a blank or generic square. Copying it into the user's icon
    /// theme makes it outlive the process, and puts it where the menu entry can find it too.
    /// </remarks>
    [SupportedOSPlatform("linux")]
    private static string? InstallLinuxIcon()
    {
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrEmpty(appDir)) return null;

        var source = Path.Combine(appDir, "mcserverlauncher.png");
        if (!File.Exists(source)) return null;

        try
        {
            var iconsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "icons", "hicolor", "256x256", "apps");
            Directory.CreateDirectory(iconsDir);

            var installed = Path.Combine(iconsDir, "mc-server-launcher.png");
            File.Copy(source, installed, overwrite: true);
            File.SetUnixFileMode(installed,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            TryRefreshIconCache();
            return installed;
        }
        catch
        {
            // Couldn't write to the icon theme: the mount path is still better than nothing for
            // this session, and the shortcut itself will work either way.
            return source;
        }
    }

    /// <summary>Nudges the icon cache so the new icon is picked up without logging out.</summary>
    private static void TryRefreshIconCache()
    {
        try
        {
            var hicolor = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "icons", "hicolor");
            var psi = new ProcessStartInfo("gtk-update-icon-cache")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(hicolor);
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { /* not installed, or a desktop that doesn't use it */ }
    }

    /// <summary>Marks the entry trusted on GNOME. Best-effort: other desktops don't need it.</summary>
    private static void TryTrust(string entry)
    {
        try
        {
            var psi = new ProcessStartInfo("gio") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("set");
            psi.ArgumentList.Add(entry);
            psi.ArgumentList.Add("metadata::trusted");
            psi.ArgumentList.Add("true");
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { /* no gio, or a desktop that doesn't use it */ }
    }

    // --- macOS ---------------------------------------------------------------------------------

    [SupportedOSPlatform("macos")]
    private static string CreateMacOs(string target, string desktop)
    {
        var link = Path.Combine(desktop, "MC Server Launcher");
        try { if (File.Exists(link) || Directory.Exists(link)) File.Delete(link); }
        catch { /* replaced below, or reported by the failure that follows */ }

        File.CreateSymbolicLink(link, target);
        return link;
    }

    // --- shared --------------------------------------------------------------------------------

    private static void RunOrThrow(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException(Localizer.Get("Msg_ShortcutFailed"));
        var error = psi.RedirectStandardError ? p.StandardError.ReadToEnd() : string.Empty;
        p.WaitForExit(15000);

        if (!p.HasExited || p.ExitCode != 0)
            throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_ShortcutFailedFmt"), error.Trim()));
    }

    /// <summary>Quotes a path for a .desktop Exec line, where spaces separate arguments.</summary>
    internal static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
}
