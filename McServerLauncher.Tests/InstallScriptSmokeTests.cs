using System.Diagnostics;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The Unix installer, actually run.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about these scripts is checked by reading them, which catches a missing token or
/// a stray <c>del</c> and nothing else. A script can be perfectly well-formed and still put the
/// jars in the wrong place, or move a player's mods somewhere and never say where.
/// </para>
/// <para>
/// So this one plants an <c>old.jar</c> in a destination, runs the real script over it, and checks
/// the three things that matter: the new mods arrived, the old one was moved aside, and
/// <b>the old one still exists</b>. <c>MCSL_MODS_DIR</c> and the "no terminal, do not ask" path are
/// partly here for this — a requirement of testability that happens to be a way out for anyone
/// whose folder is somewhere unusual.
/// </para>
/// <para>
/// Skipped where there is no bash, which on a developer's Windows box is possible and on CI is not.
/// A skip is honest; a test that quietly passes because it ran nothing is not.
/// </para>
/// </remarks>
public class InstallScriptSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcl-smoke-" + Guid.NewGuid().ToString("N"));

    public InstallScriptSmokeTests() => Directory.CreateDirectory(_root);

    /// <summary>Where bash lives, or null when it does not.</summary>
    private static string? Bash()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : null;

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\Git\bin\bash.exe",
                     @"C:\Program Files (x86)\Git\bin\bash.exe"
                 })
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    /// <summary>A Windows path as bash wants to see it: /c/Users/... rather than C:\Users\...</summary>
    private static string Posix(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;

        var forward = path.Replace('\\', '/');
        return forward.Length > 2 && forward[1] == ':'
            ? "/" + char.ToLowerInvariant(forward[0]) + forward[2..]
            : forward;
    }

    private (string Exit, string Output) Run(string bash, string script, string destination)
    {
        var info = new ProcessStartInfo(bash)
        {
            // -n would only parse; this runs it. Called as "bash script", which is how the pack
            // documents it everywhere, because that needs no execute bit and sidesteps macOS
            // quarantine into the bargain.
            Arguments = $"\"{Posix(script)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root
        };
        info.Environment["MCSL_MODS_DIR"] = Posix(destination);
        info.Environment["HOME"] = Posix(Path.Combine(_root, "home"));

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return (process.ExitCode.ToString(), output);
    }

    /// <summary>Writes the pack's Unix script to disk exactly as it goes into the zip.</summary>
    private string WriteScript()
    {
        var file = InstallScriptBuilder
            .Build("mi servidor", ServerType.Fabric, "1.21.1", new DateTime(2026, 9, 17, 18, 45, 0))
            .Single(f => f.Name == InstallScriptBuilder.UnixName);

        var path = Path.Combine(_root, file.Name);
        File.WriteAllText(path, file.Text.Replace("\r\n", "\n").Replace("\n", file.Newline));
        return path;
    }

    [Fact]
    public void TheScriptIsValidShell()
    {
        if (Bash() is not { } bash) return;

        var info = new ProcessStartInfo(bash)
        {
            Arguments = $"-n \"{Posix(WriteScript())}\"",
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        Assert.True(process.ExitCode == 0, "bash -n: " + errors);
    }

    [Fact]
    public void ItCopiesTheModsAndMovesTheOldOnesAsideWithoutDeletingThem()
    {
        if (Bash() is not { } bash) return;

        var script = WriteScript();

        // The pack, alongside the script, the way it lands once the zip is extracted.
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "sodium.jar"), "nuevo");

        // The player's folder, with something already in it that is not ours to remove.
        var destination = Path.Combine(_root, "minecraft", "mods");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "old.jar"), "lo que ya tenia");

        var (exit, output) = Run(bash, script, destination);

        Assert.True(exit == "0", output);

        // The pack arrived.
        Assert.True(File.Exists(Path.Combine(destination, "sodium.jar")), output);

        // What was there is gone from the mods folder...
        Assert.False(File.Exists(Path.Combine(destination, "old.jar")), output);

        // ...and is sitting in a sibling folder, with its contents intact. This is the assertion
        // the whole feature is judged on: a player's evening of setup, still on disk.
        var backup = Directory
            .GetDirectories(Path.Combine(_root, "minecraft"), "mods-backup-*")
            .SingleOrDefault();

        Assert.True(backup is not null, "no se ha creado la carpeta de respaldo. Salida:\n" + output);
        Assert.Equal("lo que ya tenia", File.ReadAllText(Path.Combine(backup!, "old.jar")));

        // And it said where it put it, or moving it would be no better than deleting it.
        Assert.Contains("mods-backup-", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyDestinationNeedsNoBackupFolder()
    {
        if (Bash() is not { } bash) return;

        var script = WriteScript();
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "sodium.jar"), "nuevo");

        var destination = Path.Combine(_root, "minecraft", "mods");
        Directory.CreateDirectory(destination);

        var (exit, output) = Run(bash, script, destination);

        Assert.True(exit == "0", output);
        Assert.True(File.Exists(Path.Combine(destination, "sodium.jar")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "minecraft"), "mods-backup-*"));
    }

    [Fact]
    public void RunFromInsideAnArchiveViewerItStopsAndSaysWhy()
    {
        if (Bash() is not { } bash) return;

        // The commonest mistake of all: double-clicking the script in the zip preview, where
        // nothing sits alongside it. Without this the script would cheerfully install nothing and
        // report success.
        var script = WriteScript();      // and deliberately no mods/ folder beside it
        var destination = Path.Combine(_root, "minecraft", "mods");

        var (exit, output) = Run(bash, script, destination);

        Assert.True(exit == "1", output);
        Assert.False(Directory.Exists(destination), "no debería haber tocado el destino");
    }

    [Fact]
    public void WithOneLauncherFoundItUsesThatFolderAndNotTheOfficialPath()
    {
        if (Bash() is not { } bash) return;

        // Finding .minecraft proves the official launcher was installed once, never that it is the
        // one being used — Prism, MultiMC, CurseForge and Modrinth App keep mods per instance. Here
        // the only folder that exists belongs to Prism, and nobody is around to be asked, so the
        // answer has to be that one rather than a path that is not even there.
        var script = WriteScript();
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "sodium.jar"), "nuevo");

        var home = Path.Combine(_root, "home");
        var prism = Path.Combine(home, ".local", "share", "PrismLauncher", "instances", "Survival", "minecraft", "mods");
        Directory.CreateDirectory(prism);

        var (exit, output) = RunWithoutOverride(bash, script);

        Assert.True(exit == "0", output);
        Assert.True(File.Exists(Path.Combine(prism, "sodium.jar")),
            "no ha usado la carpeta de la instancia encontrada. Salida:\n" + output);
    }

    /// <summary>Runs the script with a fake HOME and no MCSL_MODS_DIR, so detection decides.</summary>
    private (string Exit, string Output) RunWithoutOverride(string bash, string script)
    {
        var info = new ProcessStartInfo(bash)
        {
            Arguments = $"\"{Posix(script)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root
        };
        info.Environment["HOME"] = Posix(Path.Combine(_root, "home"));
        info.Environment.Remove("MCSL_MODS_DIR");

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return (process.ExitCode.ToString(), output);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
