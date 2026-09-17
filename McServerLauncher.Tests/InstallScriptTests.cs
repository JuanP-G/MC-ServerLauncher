using System.Globalization;
using System.IO.Compression;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The scripts the modpack carries, and the promise that they never delete anything.
/// </summary>
/// <remarks>
/// <para>
/// The pack used to be a zip and a page of instructions: extract it, find your mods folder, move
/// whatever is in there somewhere safe, copy these in. That third step is where somebody else's
/// mods get deleted, on their machine, following instructions this app wrote.
/// </para>
/// <para>
/// So the promise is checked against the artefact rather than described in a comment — the same
/// shape as <c>StartDependencyGateTests.TheCheckNeverTouchesTheNetwork</c>. A script that could
/// delete would still pass every functional test here; only a scan of what actually ships catches
/// somebody reaching for <c>del</c> because it was one line shorter.
/// </para>
/// </remarks>
public class InstallScriptTests
{
    private static readonly DateTime Built = new(2026, 9, 17, 18, 45, 0, DateTimeKind.Local);

    private static IReadOnlyList<ModpackWriter.TextFile> Scripts(
        ServerType type = ServerType.Fabric) =>
        InstallScriptBuilder.Build("mi servidor", type, "1.21.1", Built);

    private static string Windows(ServerType type = ServerType.Fabric) =>
        Scripts(type).Single(f => f.Name == InstallScriptBuilder.WindowsName).Text;

    private static string Unix(ServerType type = ServerType.Fabric) =>
        Scripts(type).Single(f => f.Name == InstallScriptBuilder.UnixName).Text;

    /// <summary>Runs a build under each language, so a check covers all five.</summary>
    private static void InEveryLanguage(Action<string, string> check)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { "es", "en", "pt", "fr", "de" })
            {
                CultureInfo.CurrentUICulture = new CultureInfo(culture);
                check(culture, Windows());
                check(culture, Unix());
            }
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    // --- Nothing is ever deleted ---

    [Fact]
    public void NeitherTemplateCanDeleteAnything()
    {
        // The one promise the whole feature rests on. A player runs this on their own machine, over
        // a mods folder that may be the only copy of an evening's work.
        //
        // Read against the templates rather than the finished scripts, and that distinction was
        // found the hard way: scanning the output matched the Spanish word «del» in an ordinary
        // sentence. The templates are the logic and the .resx files are prose, so the logic is
        // where this belongs — and every translated string ends up inside an echo, where a verb is
        // a word rather than a command.
        var deleting = new[] { "del ", "erase ", "rmdir", "rd /", "rm -", "Remove-Item", "unlink " };

        foreach (var name in new[] { "install-mods-windows.bat.in", "install-mods-unix.sh.in" })
        {
            var logic = string.Join("\n", InstallScriptBuilder.Template(name)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("rem ", StringComparison.Ordinal) && !l.StartsWith('#')));

            foreach (var verb in deleting)
                Assert.False(logic.Contains(verb, StringComparison.OrdinalIgnoreCase),
                    $"«{name}» contiene «{verb}»: estos scripts no borran nunca nada");
        }
    }

    [Fact]
    public void NoTranslatedWordCanBecomeACommand()
    {
        // The other half: a message is only ever echoed. Spanish «del» and «rm» in a Portuguese
        // word are words, and they have to stay words — which they do because nothing ever
        // interpolates a message anywhere except after echo or say.
        InEveryLanguage((culture, script) =>
        {
            foreach (var line in script.Split('\n').Select(l => l.Trim()))
            {
                if (line.StartsWith("echo ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("say ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("printf ", StringComparison.Ordinal)) continue;

                Assert.False(line.StartsWith("del ", StringComparison.OrdinalIgnoreCase),
                    $"«{culture}»: una línea que no es un mensaje empieza por «del»: {line}");
            }
        });
    }

    [Fact]
    public void WhatIsAlreadyThereIsMovedInstead()
    {
        Assert.Contains("move ", Windows(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mv ", Unix(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackupIsASiblingOfTheModsFolderAndNotInsideIt()
    {
        // Inside, it would be rescanned as mods — the loader reads the folder, not this script's
        // intentions — and a "backup" that the game still loads is not one.
        Assert.Contains("mods-backup-", Unix(), StringComparison.Ordinal);
        Assert.Contains("mods-backup-", Windows(), StringComparison.Ordinal);
        Assert.DoesNotContain("$destination/mods-backup-", Unix(), StringComparison.Ordinal);
    }

    // --- The mechanics of the zip ---

    [Fact]
    public void TheShellScriptHasNotOneCarriageReturn()
    {
        // A .sh with CRLF dies on its first line with «$'\r': command not found», which to the
        // player is indistinguishable from the pack being broken.
        var file = Scripts().Single(f => f.Name == InstallScriptBuilder.UnixName);

        Assert.Equal("\n", file.Newline);
        Assert.DoesNotContain('\r', Written(file));
    }

    [Fact]
    public void TheBatchFileHasThemAll()
    {
        var file = Scripts().Single(f => f.Name == InstallScriptBuilder.WindowsName);
        var text = Written(file);

        Assert.Equal("\r\n", file.Newline);
        Assert.Equal(text.Count(c => c == '\n'), CountOf(text, "\r\n"));
    }

    [Fact]
    public void TheBatchFileSwitchesToUtf8OnItsSecondLine()
    {
        // Or the accents come out as mojibake in four of the five languages. Second line, because
        // the first has to be @echo off — chcp prints a line of its own otherwise.
        var lines = Windows().Split('\n');

        Assert.StartsWith("@echo off", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("chcp 65001", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheShellScriptAsksForTheExecuteBit()
    {
        Assert.True(Scripts().Single(f => f.Name == InstallScriptBuilder.UnixName).Executable);
    }

    [Fact]
    public void TheScriptNamesAreNotTranslated()
    {
        // Non-ASCII names inside a zip depend on the archive's encoding and on the extractor
        // honouring it. A script that arrives as «instalar-mods-ma?os.sh» is worse than an English
        // name, and the instructions file — which is translated — names them both.
        InEveryLanguage((_, _) =>
        {
            Assert.Equal("install-mods-windows.bat", InstallScriptBuilder.WindowsName);
            Assert.Equal("install-mods-unix.sh", InstallScriptBuilder.UnixName);
        });
    }

    // --- Nothing left half-substituted ---

    [Fact]
    public void NoTokenSurvivesInAnyLanguage()
    {
        // A leftover @@TOKEN@@ is a line of gibberish in a script on somebody else's machine, and
        // nothing on this side would ever notice.
        InEveryLanguage((culture, script) =>
            Assert.False(script.Contains("@@", StringComparison.Ordinal),
                $"quedan tokens sin sustituir en el script en «{culture}»"));
    }

    [Fact]
    public void TheTimestampIsWorkedOutHereAndNotInTheScript()
    {
        // %DATE% in a .bat comes out in the machine's local format, and in much of Europe that
        // produces a folder name with slashes in it — which is not a folder name.
        Assert.Contains("mods-backup-20260917-1845", Windows(), StringComparison.Ordinal);
        Assert.DoesNotContain("%DATE%", Windows(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%TIME%", Windows(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLoaderCheckLooksForTheRightProfile()
    {
        Assert.Contains("fabric", Unix(ServerType.Fabric), StringComparison.Ordinal);
        Assert.Contains("neoforge", Unix(ServerType.NeoForge), StringComparison.Ordinal);
        Assert.Contains("https://neoforged.net", Unix(ServerType.NeoForge), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdvancedWayOutIsThereAndSoIsTheOneForPipes()
    {
        // MCSL_MODS_DIR is what makes the script testable, and a genuine escape hatch besides. The
        // terminal check is what stops it hanging for ever when there is nobody to answer.
        Assert.Contains("MCSL_MODS_DIR", Unix(), StringComparison.Ordinal);
        Assert.Contains("MCSL_MODS_DIR", Windows(), StringComparison.Ordinal);
        Assert.Contains("[ -t 0 ]", Unix(), StringComparison.Ordinal);
    }

    // --- No language can inject shell ---

    [Fact]
    public void NoTranslationCanCloseAQuoteOrStartACommand()
    {
        // Not a guard against an attacker — the strings are ours and in the repository. A guard
        // against an apostrophe, a backtick or a `$` in a French sentence being run instead of
        // printed, which is a class of bug worth making impossible rather than remembering.
        InEveryLanguage((culture, script) =>
        {
            foreach (var line in script.Split('\n').Where(l => l.TrimStart().StartsWith("say \"", StringComparison.Ordinal)))
            {
                Assert.False(line.Contains("$(", StringComparison.Ordinal) && !line.Contains("basename", StringComparison.Ordinal),
                    $"«{culture}»: sustitución de comandos inesperada en una línea de mensaje: {line}");
            }
        });
    }

    // --- The pack that comes out ---

    [Fact]
    public async Task ThePackCarriesBothScriptsAndTheInstructionsNameThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcl-script-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "mods");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "sodium.jar"), "x");

        try
        {
            var mods = new ViewModels.ServerModsViewModel(new ServerConfig
            {
                Name = "mi servidor", FolderPath = root, Type = ServerType.Fabric, GameVersion = "1.21.1"
            });

            var zipPath = Path.Combine(root, "pack.zip");
            await mods.BuildModpackWithSidesAsync(
                zipPath,
                new Dictionary<string, (ExportSelection.StoreSide, ExportSelection.StoreSide)>(),
                includeEverything: false, CancellationToken.None);

            using var zip = ZipFile.OpenRead(zipPath);
            Assert.NotNull(zip.GetEntry(InstallScriptBuilder.WindowsName));
            Assert.NotNull(zip.GetEntry(InstallScriptBuilder.UnixName));

            using var reader = new StreamReader(
                zip.GetEntry(Localizer.Get("Export_InstructionsFile"))!.Open());
            var instructions = reader.ReadToEnd();

            Assert.Contains(InstallScriptBuilder.WindowsName, instructions, StringComparison.Ordinal);
            Assert.Contains(InstallScriptBuilder.UnixName, instructions, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task APluginPackCarriesNoScripts()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcl-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        File.WriteAllText(Path.Combine(root, "plugins", "essentials.jar"), "x");

        try
        {
            var mods = new ViewModels.ServerModsViewModel(new ServerConfig
            {
                Name = "paper", FolderPath = root, Type = ServerType.Paper, GameVersion = "1.21.1"
            });

            var zipPath = Path.Combine(root, "pack.zip");
            await mods.BuildModpackWithSidesAsync(
                zipPath,
                new Dictionary<string, (ExportSelection.StoreSide, ExportSelection.StoreSide)>(),
                includeEverything: false, CancellationToken.None);

            using var zip = ZipFile.OpenRead(zipPath);

            // A plugin goes on a server, by hand, by whoever runs it. There is nothing to install.
            Assert.Null(zip.GetEntry(InstallScriptBuilder.WindowsName));
            Assert.Null(zip.GetEntry(InstallScriptBuilder.UnixName));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    // --- helpers ---

    /// <summary>The bytes as they land in the zip, line endings and all.</summary>
    private static string Written(ModpackWriter.TextFile file) =>
        file.Text.Replace("\r\n", "\n").Replace("\n", file.Newline);

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
