using System.IO;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace McServerLauncher.Services;

/// <summary>
/// Opens the app when the user signs in to the computer, or stops doing so.
/// </summary>
/// <remarks>
/// <para>
/// The answer to "is it on?" lives in the system, not in the app's settings: the user can take the
/// app out of Windows' startup list from Task Manager, delete the Linux autostart entry or remove
/// the macOS login item, and the switch in Settings must then show it off. So nothing here is
/// stored; it is read back every time.
/// </para>
/// <para>
/// Each system has its own place: the per-user <c>Run</c> key on Windows (no administrator needed),
/// an XDG autostart entry on Linux, a LaunchAgent on macOS. The command always carries
/// <see cref="LoginArgument"/>; whether that launch shows the window or stays in the tray is a
/// setting read when it happens, so changing it never has to touch the system again.
/// </para>
/// </remarks>
public static class LoginStartupService
{
    /// <summary>The argument a launch at sign-in carries, so the app knows it was not opened by hand.</summary>
    public const string LoginArgument = "--login";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "MC Server Launcher";

    private const string AgentLabel = "io.github.juanp-g.mcserverlauncher";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The Linux autostart entry. <c>XDG_CONFIG_HOME</c> when it is set, as the spec says.</summary>
    internal static string LinuxEntryPath
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config) || !Path.IsPathRooted(config))
                config = Path.Combine(Home, ".config");
            return Path.Combine(config, "autostart", "mc-server-launcher.desktop");
        }
    }

    private static string MacAgentPath => Path.Combine(Home, "Library", "LaunchAgents", AgentLabel + ".plist");

    /// <summary>True on a system this knows how to start the app on, and with a copy to start.</summary>
    public static bool IsSupported =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        && DesktopShortcutService.LaunchTarget is not null;

    /// <summary>Whether the system will open the app at the next sign-in.</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsEnabled();
            if (OperatingSystem.IsLinux()) return File.Exists(LinuxEntryPath);
            if (OperatingSystem.IsMacOS()) return File.Exists(MacAgentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Safe.Note("LoginStartup", "could not read whether the app opens at sign-in", ex);
        }
        return false;
    }

    /// <summary>Makes the system open this copy of the app at sign-in.</summary>
    /// <exception cref="UserFacingException">When there is no copy to point at.</exception>
    public static void Enable()
    {
        var target = DesktopShortcutService.LaunchTarget
            ?? throw new UserFacingException(Localization.Localizer.Get("Msg_ShortcutNoTarget"));

        if (OperatingSystem.IsWindows()) EnableWindows(target);
        else if (OperatingSystem.IsLinux()) EnableLinux(target);
        else if (OperatingSystem.IsMacOS()) EnableMac(target);
    }

    /// <summary>Stops the system opening the app at sign-in. Nothing happens if it was not.</summary>
    public static void Disable()
    {
        if (OperatingSystem.IsWindows()) DisableWindows();
        else if (OperatingSystem.IsLinux()) DeleteIfThere(LinuxEntryPath);
        else if (OperatingSystem.IsMacOS()) DeleteIfThere(MacAgentPath);
    }

    /// <summary>
    /// When it is on, points it at this copy again. Called at every launch.
    /// </summary>
    /// <remarks>
    /// An AppImage that was moved, or a portable copy run from another folder, leaves the old entry
    /// pointing at nothing, and the next sign-in silently opens no app. Rewriting it while it is on
    /// costs one small write and keeps it true. Never turns it on.
    /// </remarks>
    public static void Refresh()
    {
        try
        {
            if (IsSupported && IsEnabled()) Enable();
        }
        catch (Exception ex)
        {
            Safe.Note("LoginStartup", "could not point the sign-in entry at this copy", ex);
        }
    }

    /// <summary>Whether <paramref name="args"/> is a launch made by the system at sign-in.</summary>
    public static bool IsLoginLaunch(IEnumerable<string>? args) =>
        args?.Any(a => string.Equals(a, LoginArgument, StringComparison.OrdinalIgnoreCase)) == true;

    // --- Windows -------------------------------------------------------------------------------

    /// <summary>The command Windows runs: the path in quotes, so one with spaces is not split.</summary>
    internal static string WindowsCommand(string target) => $"\"{target}\" {LoginArgument}";

    [SupportedOSPlatform("windows")]
    private static bool WindowsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string { Length: > 0 }) return false;

        // Task Manager's Startup tab does not delete the entry: it marks it here. An odd first byte
        // is "disabled" (02/06 enabled, 03/07 disabled, the rest a timestamp of when).
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return !(approved?.GetValue(ValueName) is byte[] { Length: > 0 } state && IsDisabledMark(state));
    }

    /// <summary>Whether a <c>StartupApproved</c> value says the user switched the entry off.</summary>
    internal static bool IsDisabledMark(byte[] state) => state.Length > 0 && (state[0] & 1) == 1;

    [SupportedOSPlatform("windows")]
    private static void EnableWindows(string target)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, WindowsCommand(target), RegistryValueKind.String);

        // Turned back on from the app after being turned off in Task Manager: without clearing the
        // mark, the entry is there and Windows still skips it.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        if (approved?.GetValue(ValueName) is byte[] state && IsDisabledMark(state))
            approved.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    [SupportedOSPlatform("windows")]
    private static void DisableWindows()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    // --- Linux ---------------------------------------------------------------------------------

    /// <summary>The autostart entry for <paramref name="target"/>.</summary>
    internal static string LinuxEntry(string target) => string.Join('\n',
        "[Desktop Entry]",
        "Type=Application",
        "Name=MC Server Launcher",
        "Comment=Manage your Minecraft servers",
        "Exec=" + DesktopShortcutService.Quote(target) + " " + LoginArgument,
        "Terminal=false",
        "X-GNOME-Autostart-enabled=true",
        "");

    private static void EnableLinux(string target)
    {
        var path = LinuxEntryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, LinuxEntry(target));
    }

    // --- macOS ---------------------------------------------------------------------------------

    /// <summary>
    /// The LaunchAgent for <paramref name="target"/>: a bundle is opened with <c>open</c>, which is
    /// how macOS expects an app to be started; a bare executable is run directly.
    /// </summary>
    internal static string MacAgent(string target)
    {
        var program = target.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? new[] { "/usr/bin/open", "-a", target, "--args", LoginArgument }
            : new[] { target, LoginArgument };
        var arguments = string.Concat(program.Select(a => $"\n        <string>{SecurityElement.Escape(a)}</string>"));

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{AgentLabel}</string>
                <key>ProgramArguments</key>
                <array>{arguments}
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>

            """;
    }

    private static void EnableMac(string target)
    {
        var path = MacAgentPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, MacAgent(target));
    }

    private static void DeleteIfThere(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
