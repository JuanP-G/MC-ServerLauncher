using System.Diagnostics;
using System.Security.Cryptography;

namespace McServerLauncher.Services;

/// <summary>
/// The second copy of the app that runs the Windows installer, once the first has exited.
/// </summary>
/// <remarks>
/// <para>
/// The installer asks for administrator rights, and it used to be started by a <c>.cmd</c> written
/// next to it in the temporary folder, minutes after its checksum and signature were checked. The
/// folder name is random, which stops somebody planting a file there beforehand; it does not stop a
/// program already running as this user from replacing the installer or the <c>.cmd</c> in the
/// meantime, and the replacement would then run with the administrator rights the user just granted.
/// </para>
/// <para>
/// No file in a folder the user can write to can close that, so nothing is left to replace. The
/// helper is the app's own executable, which lives where only an administrator can write, started
/// with the verified SHA-256 on its command line (a running process's arguments cannot be edited).
/// It waits for the app to exit, opens the installer so that nobody else can write to, rename or
/// delete it, hashes it through that same handle, and starts it <em>without letting go</em>: the bytes
/// that are hashed are the bytes that run.
/// </para>
/// <para>
/// If anything is off — another file, no file, the installer refusing to start, the user declining
/// the elevation prompt — the app is started again, so a failed update leaves the old version
/// running instead of nothing.
/// </para>
/// </remarks>
internal static class UpdateHelper
{
    /// <summary>The first argument that makes the executable act as the helper. See <see cref="Run"/>.</summary>
    public const string Switch = "--apply-update";

    /// <summary>What <see cref="Apply"/> returns when it did not run the installer.</summary>
    public const int NotTheVerifiedFile = 2, ParentStillRunning = 3, NotAnUpdatePackage = 4;

    /// <summary>The generic failure: the installer could not be read or started.</summary>
    public const int Failed = 1;

    /// <summary>The installer's switches: no window, no dialogs, no reboot.</summary>
    private const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART";

    /// <summary>Whether this start is the helper's, which must be decided before anything else runs.</summary>
    public static bool IsHelper(string[] args) => args.Length > 0 && args[0] == Switch;

    /// <summary>The helper's whole life, from <c>Main</c>: <c>--apply-update &lt;pid&gt; &lt;sha256&gt; &lt;installer&gt;</c>.</summary>
    /// <returns>The process's exit code.</returns>
    public static int Run(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[1], out var parentPid)) return Failed;

        return Apply(parentPid, args[3], args[2], StartInstaller, Relaunch, Log,
            parentWait: TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Waits for the app to exit, checks <paramref name="installer"/> against
    /// <paramref name="expectedSha256"/> and runs it while holding it.
    /// </summary>
    /// <param name="parentPid">The app that is exiting.</param>
    /// <param name="installer">The package, in a folder of its own under the temporary folder.</param>
    /// <param name="expectedSha256">What the app verified it to be.</param>
    /// <param name="runInstaller">Runs it and returns its exit code. Called while the file is held.</param>
    /// <param name="relaunch">Starts the app again; called when the installer did not do its job.</param>
    /// <param name="log">Says what happened, for the log.</param>
    /// <param name="parentWait">How long to wait for the app to exit.</param>
    /// <param name="tempRoot">Where packages are downloaded; the temporary folder when null.</param>
    /// <returns>The installer's exit code, or one of the constants here when it was not run.</returns>
    internal static int Apply(int parentPid, string installer, string expectedSha256,
        Func<string, int> runInstaller, Action relaunch, Action<string> log, TimeSpan parentWait,
        string? tempRoot = null)
    {
        // This switch must not be a way to start any program: only a package the app itself put in
        // one of its own download folders.
        if (!IsPackagePath(installer, tempRoot ?? Path.GetTempPath()))
        {
            log($"Update helper: refused {installer}, which is not a downloaded package.");
            return NotAnUpdatePackage;
        }

        if (!ParentExited(parentPid, parentWait))
        {
            log("Update helper: the app did not exit; the installer was not run.");
            return ParentStillRunning;
        }

        try
        {
            // Read sharing only: from here nobody can write to, rename or delete the file until this
            // handle closes, which is after the installer has finished.
            using var held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);

            var actual = Convert.ToHexString(SHA256.HashData(held));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                log("Update helper: the installer is not the file that was verified; it was not run.");
                relaunch();
                return NotTheVerifiedFile;
            }

            var code = runInstaller(installer);
            if (code != 0)
            {
                log($"Update helper: the installer ended with code {code}.");
                relaunch();
            }
            return code;
        }
        catch (Exception ex)
        {
            log($"Update helper: could not run the installer: {ex.Message}");
            relaunch();
            return Failed;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a file directly inside one of the folders
    /// <see cref="AppUpdateCoordinator.NewPackageFolder"/> makes: <c>mcsl-…</c> under
    /// <paramref name="tempRoot"/>.
    /// </summary>
    internal static bool IsPackagePath(string path, string tempRoot)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder is null) return false;

            var parent = Path.GetDirectoryName(folder);
            var root = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return parent is not null
                   && string.Equals(parent, root, comparison)
                   && Path.GetFileName(folder).StartsWith(SelfUpdater.PackageFolderPrefix, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Waits for a process to end. A process that is already gone is the answer too.</summary>
    private static bool ParentExited(int pid, TimeSpan wait)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            return parent.WaitForExit(wait);
        }
        catch (ArgumentException)
        {
            return true;   // no such process: it already exited
        }
    }

    /// <summary>Runs the installer, elevating through the prompt its manifest asks for, and waits for it.</summary>
    private static int StartInstaller(string installer)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = installer,
            Arguments = InstallerArguments,
            UseShellExecute = true   // what lets Windows show the elevation prompt for its manifest
        }) ?? throw new InvalidOperationException("The installer did not start.");

        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Starts the app again, as it was started the first time.</summary>
    private static void Relaunch()
    {
        var (file, leading) = SelfCommand();
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false };
        foreach (var arg in leading) psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    /// <summary>
    /// How to start this program again: its own executable, or <c>dotnet</c> and the assembly when it
    /// was started with <c>dotnet McServerLauncher.dll</c>.
    /// </summary>
    internal static (string File, IReadOnlyList<string> LeadingArguments) SelfCommand()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var viaDotnet = Path.GetFileNameWithoutExtension(path)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        return viaDotnet
            ? (path, new[] { System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "" })
            : (path, Array.Empty<string>());
    }

    private static void Log(string message)
    {
        ConsoleLogService.Shared.Log("Update", message);
        ConsoleLogService.Shared.Flush();
    }
}
