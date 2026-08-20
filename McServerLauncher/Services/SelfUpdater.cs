using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Replaces the running application with an already downloaded and verified package, then restarts
/// it — the same "press Update and it just happens" behaviour on every platform.
/// <para>
/// Each platform ships differently, so each is applied differently: Windows runs the silent
/// installer, Linux swaps the AppImage file the app is running from, and macOS mounts the .dmg and
/// replaces the .app bundle. What they share is the shape: nothing is touched until a complete,
/// checksum-verified package exists on disk, and any failure leaves the current install working.
/// </para>
/// </summary>
public static class SelfUpdater
{
    /// <summary>Why an in-place update isn't possible here, or null when it is.</summary>
    /// <remarks>
    /// Not every install can replace itself: an AppImage moved into <c>/opt</c> by root, a macOS
    /// bundle in a read-only location, or the app started straight from <c>dotnet run</c>. Those
    /// cases fall back to opening the release page, which is what every platform but Windows used
    /// to do unconditionally.
    /// </remarks>
    public static string? Blocker
    {
        get
        {
            if (OperatingSystem.IsWindows()) return null;
            if (OperatingSystem.IsLinux())
                return AppImagePath is null ? Localizer.Get("Msg_UpdateNotAppImage") : null;
            if (OperatingSystem.IsMacOS())
                return AppBundlePath is null ? Localizer.Get("Msg_UpdateNotBundle") : null;
            return Localizer.Get("Msg_UpdateUnsupportedPlatform");
        }
    }

    public static bool CanUpdateInPlace => Blocker is null;

    /// <summary>What to call the downloaded package on disk.</summary>
    public static string PackageFileName(string? assetName) =>
        string.IsNullOrWhiteSpace(assetName)
            ? OperatingSystem.IsWindows() ? "MC-ServerLauncher-Setup.exe"
                : OperatingSystem.IsLinux() ? "MC-ServerLauncher.AppImage"
                : "MC-ServerLauncher.dmg"
            : Path.GetFileName(assetName);

    /// <summary>
    /// Applies <paramref name="packagePath"/> and relaunches. The caller must have verified its
    /// checksum and stopped the servers first.
    /// </summary>
    public static void Apply(string packagePath)
    {
        if (OperatingSystem.IsWindows()) ApplyWindows(packagePath);
        else if (OperatingSystem.IsLinux()) ApplyLinux(packagePath);
        else if (OperatingSystem.IsMacOS()) ApplyMacOs(packagePath);
        else throw new PlatformNotSupportedException();
    }

    // --- Windows -------------------------------------------------------------------------------

    private static void ApplyWindows(string installer)
    {
        // A helper that waits for THIS app to exit before running the installer: closing too soon
        // raced with UAC elevation and the silent install never applied.
        var helper = Path.Combine(Path.GetDirectoryName(installer)!, "mcsl-update.cmd");
        File.WriteAllText(helper,
            "@echo off\r\n" +
            ":wait\r\n" +
            "tasklist /FI \"IMAGENAME eq McServerLauncher.exe\" 2>nul | find /I \"McServerLauncher.exe\" >nul\r\n" +
            "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )\r\n" +
            "\"" + installer + "\" /SILENT /SUPPRESSMSGBOXES /NORESTART\r\n");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"" + helper + "\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    // --- Linux ---------------------------------------------------------------------------------

    /// <summary>The AppImage this process runs from, or null when it isn't one.</summary>
    /// <remarks>The AppImage runtime exports APPIMAGE as the absolute path of the file itself.</remarks>
    private static string? AppImagePath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("APPIMAGE");
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ApplyLinux(string package)
    {
        var target = AppImagePath ?? throw new InvalidOperationException(Localizer.Get("Msg_UpdateNotAppImage"));

        // Renaming over a running AppImage is safe: the running process keeps the old inode alive
        // and carries on from it, while the directory entry already points at the new build.
        // The staged copy sits beside the target so the rename stays on one filesystem — a
        // cross-device move degrades into a copy, and a copy can be interrupted halfway.
        var staged = Path.Combine(Path.GetDirectoryName(target)!,
            "." + Path.GetFileName(target) + ".new-" + Environment.ProcessId);

        try
        {
            File.Copy(package, staged, overwrite: true);
            File.SetUnixFileMode(staged,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            File.Move(staged, target, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best-effort */ }
            // The AppImage in use was never touched, so the app still runs: say why and let the
            // caller offer the download page instead.
            throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_UpdateCannotReplaceFmt"), target, ex.Message), ex);
        }

        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = false });
    }

    // --- macOS ---------------------------------------------------------------------------------

    /// <summary>The .app bundle this process runs from, or null when it isn't inside one.</summary>
    private static string? AppBundlePath
    {
        get
        {
            // …/MC Server Launcher.app/Contents/MacOS/McServerLauncher
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
                if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    return dir.FullName;
            return null;
        }
    }

    [SupportedOSPlatform("macos")]
    private static void ApplyMacOs(string dmg)
    {
        var bundle = AppBundlePath ?? throw new InvalidOperationException(Localizer.Get("Msg_UpdateNotBundle"));

        // A bundle can't replace itself from inside while it runs, so a detached script does it
        // once this process is gone. It swaps by rename and puts the old bundle back if the copy
        // fails, so an interrupted update can never leave the user with no app at all.
        var script = Path.Combine(Path.GetDirectoryName(dmg)!, "mcsl-update.sh");
        File.WriteAllText(script, BuildMacUpdateScript(bundle, dmg));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { script },
            UseShellExecute = false
        });
    }

    /// <summary>The script that swaps the bundle once this process is gone.</summary>
    /// <remarks>
    /// A function of its own so its syntax can be checked without a Mac: a typo here would only
    /// ever surface as an update that silently did nothing, on someone else's machine.
    /// </remarks>
    internal static string BuildMacUpdateScript(string bundle, string dmg) =>
        $"""
        #!/bin/bash
        APP={Quote(bundle)}
        DMG={Quote(dmg)}
        MOUNT=$(mktemp -d)
        BAK="$APP.old-$$"

        # Wait for the app that launched us to exit (give up after a minute).
        for _ in $(seq 1 60); do
          pgrep -x McServerLauncher >/dev/null || break
          sleep 1
        done

        hdiutil attach "$DMG" -nobrowse -quiet -mountpoint "$MOUNT" || exit 1
        NEW=$(find "$MOUNT" -maxdepth 1 -name '*.app' -print -quit)

        if [ -n "$NEW" ] && mv "$APP" "$BAK"; then
          if ditto "$NEW" "$APP"; then
            xattr -dr com.apple.quarantine "$APP" 2>/dev/null
            rm -rf "$BAK"
          else
            rm -rf "$APP"
            mv "$BAK" "$APP"      # rollback: the old version beats no version
          fi
        fi

        hdiutil detach "$MOUNT" -quiet || true
        rmdir "$MOUNT" 2>/dev/null
        open "$APP"
        """;

    /// <summary>Single-quotes a path for the shell, so spaces in "MC Server Launcher.app" survive.</summary>
    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
