using System.IO.Compression;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using Store = McServerLauncher.Services.ExportSelection.StoreSide;

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
/// <para>
/// Everything below goes through <c>BuildModpackWithSidesAsync</c>, which takes the store's answers
/// as an argument. No test here reaches api.modrinth.com: the result would otherwise depend on a
/// third party's uptime and on project data anyone can edit.
/// </para>
/// </remarks>
public class ModpackExportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcl-export-" + Guid.NewGuid().ToString("N"));

    public ModpackExportTests() => Directory.CreateDirectory(_root);

    private string Destination => Path.Combine(_root, "pack.zip");

    /// <summary>What an export that could not reach the store sees.</summary>
    private static readonly IReadOnlyDictionary<string, (Store Client, Store Server)> NoStore =
        new Dictionary<string, (Store, Store)>();

    private string _content = string.Empty;

    /// <summary>A server folder whose content folder is ready to have jars written into it.</summary>
    private ServerConfig Server(ServerType type)
    {
        var folder = Path.Combine(_root, "server-" + Guid.NewGuid().ToString("N")[..8]);
        _content = Path.Combine(folder, ServerTypeCatalog.ContentFolder(type));
        Directory.CreateDirectory(_content);

        return new ServerConfig
        {
            Name = "mi servidor", FolderPath = folder, Type = type, GameVersion = "1.21.1"
        };
    }

    /// <summary>A file in the content folder that is not a readable jar. Declares nothing.</summary>
    private void Blob(string fileName) =>
        File.WriteAllText(Path.Combine(_content, fileName), "pretend this is " + fileName);

    /// <summary>A real jar carrying a fabric.mod.json, which is where the side comes from.</summary>
    private void FabricJar(string fileName, string id, string? environment = null, string? depends = null)
    {
        var fields = $"\"schemaVersion\":1,\"id\":\"{id}\",\"version\":\"1.0.0\"";
        if (environment is not null) fields += $",\"environment\":\"{environment}\"";
        if (depends is not null) fields += $",\"depends\":{{\"{depends}\":\"*\"}}";

        using var zip = new ZipArchive(
            File.Create(Path.Combine(_content, fileName)), ZipArchiveMode.Create);
        using var entry = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
        entry.Write("{" + fields + "}");
    }

    private Task<ServerModsViewModel.ModpackResult> Build(
        ServerModsViewModel mods,
        bool includeEverything = false,
        IReadOnlyDictionary<string, (Store Client, Store Server)>? sides = null) =>
        mods.BuildModpackWithSidesAsync(
            Destination, sides ?? NoStore, includeEverything, CancellationToken.None);

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

    // --- The pack itself ---

    [Fact]
    public async Task ThePackHoldsTheJarsAndTheInstructions()
    {
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        Blob("lithium.jar");
        Blob("sodium.jar");

        var result = await Build(mods);

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
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        Blob("lithium.jar");

        await Build(mods);

        Assert.Equal("pretend this is lithium.jar", EntryText(Destination, "mods/lithium.jar"));
    }

    [Fact]
    public async Task ExportingAgainReplacesThePackInsteadOfAddingToIt()
    {
        // The old code deleted the destination by hand before zipping. Getting this wrong is not
        // obvious from the outside: the pack keeps working, and simply still contains a mod that
        // was removed from the server — which is a crash on the player's machine, not on ours.
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        Blob("lithium.jar");
        Blob("quitado.jar");

        await Build(mods);
        Assert.Contains("mods/quitado.jar", EntryNames(Destination));

        File.Delete(Path.Combine(_content, "quitado.jar"));
        await Build(mods);

        Assert.DoesNotContain("mods/quitado.jar", EntryNames(Destination));
    }

    [Fact]
    public async Task AServerWithNoModsStillProducesAPackThatOpens()
    {
        // Worth saying out loud: an empty pack is a readable zip with the instructions in it, not
        // a corrupt file or an exception halfway through writing one.
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));

        var result = await Build(mods);

        Assert.Empty(result.Included);
        Assert.Equal(new[] { Localizer.Get("Export_InstructionsFile") }, EntryNames(Destination));
    }

    [Fact]
    public async Task TheInstructionsNameTheServerAndItsVersion()
    {
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        Blob("lithium.jar");

        await Build(mods);
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
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        Blob("lithium.jar");

        await Build(mods);

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

    // --- What gets left out ---

    [Fact]
    public async Task AJarThatSaysItIsServerOnlyDoesNotTravel()
    {
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        FabricJar("geyser.jar", "geyser", environment: "server");
        FabricJar("sodium.jar", "sodium", environment: "client");

        var result = await Build(mods);

        Assert.Equal(new[] { "geyser.jar" }, result.Excluded);
        Assert.DoesNotContain("mods/geyser.jar", EntryNames(Destination));
        Assert.Contains("mods/sodium.jar", EntryNames(Destination));
    }

    [Fact]
    public async Task TheStoreCanLeaveOutAJarThatSaidNothingItself()
    {
        // The common case, and the only one the store is actually needed for: most authors never
        // fill the field in, so without it Floodgate would ride along in every pack.
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        FabricJar("floodgate.jar", "floodgate");
        FabricJar("sodium.jar", "sodium");

        var result = await Build(mods, sides: new Dictionary<string, (Store, Store)>
        {
            ["floodgate.jar"] = (Store.Unsupported, Store.Required)
        });

        Assert.Equal(new[] { "floodgate.jar" }, result.Excluded);
    }

    [Fact]
    public async Task ALibraryAKeptModNeedsTravelsEvenIfItLooksServerOnly()
    {
        // The crash this must not cause, checked end to end and not only in the table: the library
        // declares server, a client mod in the same folder depends on it, and dropping it would
        // kill the player's Minecraft on startup with nothing pointing back here.
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        FabricJar("libreria.jar", "libreria", environment: "server");
        FabricJar("mimod.jar", "mimod", environment: "client", depends: "libreria");

        var result = await Build(mods);

        Assert.Empty(result.Excluded);
        Assert.Contains("mods/libreria.jar", EntryNames(Destination));
    }

    [Fact]
    public async Task APluginServerLeavesNothingOut()
    {
        // Plugins are server-side by definition, so the table would empty the pack. It never runs:
        // a plugin pack is a zip a player can do nothing with, which is why the button is hidden.
        var mods = new ServerModsViewModel(Server(ServerType.Paper));
        Blob("essentials.jar");
        Blob("vault.jar");

        var result = await Build(mods);

        Assert.Empty(result.Excluded);
        Assert.Contains("plugins/essentials.jar", EntryNames(Destination));
    }

    [Fact]
    public async Task IncludingThemAnywayPutsThemBack()
    {
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        FabricJar("geyser.jar", "geyser", environment: "server");
        FabricJar("sodium.jar", "sodium", environment: "client");

        Assert.Single((await Build(mods)).Excluded);

        var result = await Build(mods, includeEverything: true);

        Assert.Empty(result.Excluded);
        Assert.Contains("mods/geyser.jar", EntryNames(Destination));
    }

    [Fact]
    public async Task ThePackTellsThePlayerWhatIsNotInIt()
    {
        // The pack has to explain itself to whoever receives it, not only to whoever made it. A
        // player who counts the jars and finds one missing should read why in the box, not wonder.
        var mods = new ServerModsViewModel(Server(ServerType.Fabric));
        FabricJar("geyser.jar", "geyser", environment: "server");
        FabricJar("sodium.jar", "sodium", environment: "client");

        await Build(mods);
        var text = EntryText(Destination, Localizer.Get("Export_InstructionsFile"));

        Assert.Contains("geyser.jar", text);
    }

    // --- The notice ---

    [Fact]
    public void TheNoticeNamesWhatItLeftOut()
    {
        // Naming them is what makes it actionable instead of unsettling: "3 files were left out" on
        // its own is something to worry about; three file names is something to check.
        var notice = ServerModsViewModel.NoticeFor(new ServerModsViewModel.ModpackResult(
            new[] { "sodium.jar" }, new[] { "geyser.jar", "floodgate.jar" }, AskedTheStore: true));

        Assert.Contains("geyser.jar", notice);
        Assert.Contains("floodgate.jar", notice);
        Assert.DoesNotContain("{0}", notice);
        Assert.DoesNotContain(Localizer.Get("Export_NoticeOffline"), notice);
    }

    [Fact]
    public void TheNoticeSaysWhenThePackWasBuiltWithoutTheStore()
    {
        // The pack does come out different offline — fewer jars are left out. Said out loud,
        // because a difference the user can see is one they can decide about.
        var notice = ServerModsViewModel.NoticeFor(new ServerModsViewModel.ModpackResult(
            new[] { "sodium.jar" }, Array.Empty<string>(), AskedTheStore: false));

        Assert.Contains(Localizer.Get("Export_NoticeOffline"), notice);
    }

    [Fact]
    public void AnExportThatLeftNothingOutStillSaysSo()
    {
        var notice = ServerModsViewModel.NoticeFor(new ServerModsViewModel.ModpackResult(
            new[] { "a.jar", "b.jar" }, Array.Empty<string>(), AskedTheStore: true));

        Assert.False(string.IsNullOrWhiteSpace(notice));
        Assert.DoesNotContain("{0}", notice);
    }

    // --- The panel ---

    [Fact]
    public void TheExportButtonIsHiddenOnPluginServers()
    {
        var button = Regex.Matches(Markup(), @"<Button\b[\s\S]*?>")
            .Select(m => m.Value)
            .Single(v => v.Contains("{Binding ExportModpackCommand}", StringComparison.Ordinal));

        Assert.Contains("IsVisible=\"{Binding !IsPluginBased}\"", button);
    }

    [Fact]
    public void EveryNameTheExportNoticeBindsToExistsOnTheViewModel()
    {
        // ServerModsView.axaml is x:CompileBindings="False": a mistyped name raises nothing, and a
        // notice that never appears looks exactly like an export that left nothing out.
        var panel = Markup();
        var start = panel.IndexOf("{Binding HasExportNotice}", StringComparison.Ordinal);
        Assert.True(start >= 0, "el aviso del export ya no está en ServerModsView.axaml");

        start = panel.LastIndexOf("<Border", start, StringComparison.Ordinal);
        var end = panel.IndexOf("</Border>", start, StringComparison.Ordinal);

        var vm = typeof(ServerModsViewModel);
        foreach (Match match in Regex.Matches(panel[start..end], @"\{Binding ([A-Za-z0-9_]+)\}"))
        {
            var name = match.Groups[1].Value;
            Assert.True(vm.GetProperty(name) is not null,
                $"ServerModsViewModel no tiene ninguna propiedad «{name}», que ServerModsView.axaml enlaza");
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

    private static string Markup() => File.ReadAllText(Path.Combine(
        LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "ServerModsView.axaml"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
