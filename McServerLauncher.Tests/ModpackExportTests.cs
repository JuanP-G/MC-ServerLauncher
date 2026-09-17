using System.IO.Compression;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Building the zip a server's players are handed.
/// </summary>
/// <remarks>
/// <para>
/// None of this was reachable before. The export began by opening a file picker, so it returned on
/// its second line without a main window and there was no way to look at what it had produced —
/// a feature that copies every jar on the server into a zip, with no test of any kind.
/// </para>
/// <para>
/// It also assembled the pack in a folder under <c>%TEMP%</c> and zipped that, so a 2 GB modpack
/// needed 2 GB of scratch space, and <c>ZipFile.CreateFromDirectory</c> gave no access to the
/// entries — which is why the line endings of a file inside the pack could not be chosen.
/// </para>
/// </remarks>
public class ModpackExportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcl-export-" + Guid.NewGuid().ToString("N"));

    public ModpackExportTests() => Directory.CreateDirectory(_root);

    private string Destination => Path.Combine(_root, "pack.zip");

    /// <summary>A server folder with the given jars in the folder its type uses.</summary>
    private ServerConfig Server(ServerType type, params string[] jars)
    {
        var folder = Path.Combine(_root, "server-" + Guid.NewGuid().ToString("N")[..8]);
        var content = Path.Combine(folder, ServerTypeCatalog.ContentFolder(type));
        Directory.CreateDirectory(content);

        foreach (var jar in jars)
            File.WriteAllText(Path.Combine(content, jar), "pretend this is " + jar);

        return new ServerConfig
        {
            Name = "mi servidor", FolderPath = folder, Type = type, GameVersion = "1.21.1"
        };
    }

    private static string[] EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static string EntryText(string zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(name);
        Assert.True(entry is not null, $"el zip no lleva «{name}»");

        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task ThePackHoldsTheJarsAndTheInstructions()
    {
        var mods = new ServerModsViewModel(Server(ServerType.Fabric, "lithium.jar", "sodium.jar"));

        var result = await mods.BuildModpackAsync(Destination, CancellationToken.None);

        Assert.Equal(new[] { "lithium.jar", "sodium.jar" }, result.Included);
        Assert.Equal(
            new[] { Localizer.Get("Export_InstructionsFile"), "mods/lithium.jar", "mods/sodium.jar" }
                .OrderBy(n => n, StringComparer.Ordinal),
            EntryNames(Destination));
    }

    [Fact]
    public async Task TheJarsArriveWithTheirContentsIntact()
    {
        // The refactor reads each jar straight out of the server folder instead of copying it to a
        // scratch directory first. Worth one test that the bytes still make the trip.
        var mods = new ServerModsViewModel(Server(ServerType.Fabric, "lithium.jar"));

        await mods.BuildModpackAsync(Destination, CancellationToken.None);

        Assert.Equal("pretend this is lithium.jar", EntryText(Destination, "mods/lithium.jar"));
    }

    [Fact]
    public async Task APluginServerPacksThePluginsFolder()
    {
        var mods = new ServerModsViewModel(Server(ServerType.Paper, "essentials.jar"));

        await mods.BuildModpackAsync(Destination, CancellationToken.None);

        Assert.Contains("plugins/essentials.jar", EntryNames(Destination));
    }

    [Fact]
    public async Task ExportingAgainReplacesThePackInsteadOfAddingToIt()
    {
        // The old code deleted the destination by hand before zipping. Getting this wrong is not
        // obvious from the outside: the pack keeps working, and simply still contains a mod that
        // was removed from the server — which is a crash on the player's machine, not on ours.
        var config = Server(ServerType.Fabric, "lithium.jar", "quitado.jar");
        var mods = new ServerModsViewModel(config);

        await mods.BuildModpackAsync(Destination, CancellationToken.None);
        Assert.Contains("mods/quitado.jar", EntryNames(Destination));

        File.Delete(Path.Combine(config.FolderPath, "mods", "quitado.jar"));
        await mods.BuildModpackAsync(Destination, CancellationToken.None);

        Assert.DoesNotContain("mods/quitado.jar", EntryNames(Destination));
    }

    [Fact]
    public async Task AServerWithNoModsStillProducesAPackThatOpens()
    {
        // Worth saying out loud: an empty pack is a readable zip with the instructions in it, not
        // a corrupt file or an exception halfway through writing one.
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));

        var result = await mods.BuildModpackAsync(Destination, CancellationToken.None);

        Assert.Empty(result.Included);
        Assert.Equal(new[] { Localizer.Get("Export_InstructionsFile") }, EntryNames(Destination));
    }

    [Fact]
    public async Task TheInstructionsNameTheServerAndItsVersion()
    {
        var mods = new ServerModsViewModel(Server(ServerType.Fabric, "lithium.jar"));

        await mods.BuildModpackAsync(Destination, CancellationToken.None);
        var text = EntryText(Destination, Localizer.Get("Export_InstructionsFile"));

        Assert.Contains("mi servidor", text);
        Assert.Contains("1.21.1", text);
        Assert.DoesNotContain("{0}", text);     // an unformatted placeholder reaching the player
    }

    [Fact]
    public async Task NoTextInThePackStartsWithAByteOrderMark()
    {
        // A BOM is invisible in an editor and fatal in two places the pack is about to acquire: on
        // the first line of a .bat it is printed before @echo off can take effect, and ahead of a
        // shell script's #! it stops the file being recognised as a script at all.
        var mods = new ServerModsViewModel(Server(ServerType.Fabric, "lithium.jar"));

        await mods.BuildModpackAsync(Destination, CancellationToken.None);

        using var zip = ZipFile.OpenRead(Destination);
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith(".jar", StringComparison.Ordinal)))
        {
            using var stream = entry.Open();
            var head = new byte[3];
            var read = stream.Read(head, 0, 3);

            Assert.False(read == 3 && head is [0xEF, 0xBB, 0xBF],
                $"«{entry.FullName}» empieza por BOM");
        }
    }

    [Fact]
    public void TheWriterNeverNeedsScratchSpace()
    {
        // The reason a 2 GB pack used to need 2 GB free on the system drive. Checked against the
        // file rather than promised in a comment, because the cheapest way to write the next text
        // file into the pack is to write it to %TEMP% first and add it, and nothing else would
        // notice.
        var source = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Services", "ModpackWriter.cs"));

        foreach (var forbidden in new[] { "GetTempPath", "GetTempFileName" })
            Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                $"ModpackWriter menciona «{forbidden}»: el pack se escribe entrada a entrada, sin copias");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
