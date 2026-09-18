using System.Reflection;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Updates found by one check, still there after the list is rebuilt.
/// </summary>
/// <remarks>
/// <para>
/// The report: check for updates, five come up, update one — and the buttons on the other four
/// vanish, so the check has to be run again for every single mod. The newer versions lived on the
/// rows, and the list is rebuilt from disk after every update, every toggle and every delete.
/// </para>
/// <para>
/// Nothing here reaches Modrinth. The pending updates are seeded the same way the real check
/// records them, and everything after that is the refresh path the bug was on.
/// </para>
/// </remarks>
public class PendingUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcl-pending-" + Guid.NewGuid().ToString("N"));

    private string ModsFolder => Path.Combine(_root, "mods");

    public PendingUpdateTests() => Directory.CreateDirectory(ModsFolder);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private ServerConfig Config() => new()
    {
        Name = "actualizaciones", FolderPath = _root, Type = ServerType.Fabric, GameVersion = "1.21.1"
    };

    private string Jar(string name, string content = "v1")
    {
        var path = Path.Combine(ModsFolder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ModUpdateInfo Newer(string file) =>
        new("2.0.0", "https://cdn.example/" + file, file, null, null);

    /// <summary>A panel whose last check found an update for each of these jars.</summary>
    private static ServerModsViewModel CheckedWith(ServerConfig config, params string[] paths)
    {
        var mods = new ServerModsViewModel(config);
        mods.BeginPendingUpdates();
        foreach (var path in paths)
            mods.RememberUpdate(path, Newer(Path.GetFileName(path)));
        mods.ReloadInstalled();
        return mods;
    }

    private static string[] WithUpdates(ServerModsViewModel mods) =>
        mods.InstalledMods.Where(m => m.UpdateAvailable).Select(m => m.FileName).OrderBy(n => n).ToArray();

    [Fact]
    public void UpdatingOneModLeavesTheOthersOnOffer()
    {
        // The report, step by step: five updates, one done, the list rebuilt from disk.
        var paths = Enumerable.Range(1, 5).Select(i => Jar($"mod{i}.jar")).ToArray();
        var mods = CheckedWith(Config(), paths);
        Assert.Equal(5, mods.PendingUpdateCount);

        // What an update leaves behind: the old jar gone, the new version under its own name.
        File.Delete(paths[0]);
        Jar("mod1-2.0.0.jar", "v2");
        mods.ReloadInstalled();

        Assert.Equal(new[] { "mod2.jar", "mod3.jar", "mod4.jar", "mod5.jar" }, WithUpdates(mods));
        Assert.Equal(4, mods.PendingUpdateCount);
    }

    [Fact]
    public void TheLineUnderTheListCountsWhatIsStillPending()
    {
        // The refresh used to blank it, because the rows it had just rebuilt had lost track.
        var paths = new[] { Jar("a.jar"), Jar("b.jar"), Jar("c.jar") };
        var mods = CheckedWith(Config(), paths);

        File.Delete(paths[0]);
        mods.ReloadInstalled();

        Assert.Equal(string.Format(Localizer.Get("Msg_UpdatesFoundFmt"), 2), mods.UpdateStatus);
    }

    [Fact]
    public void DisablingAModKeepsItsUpdate()
    {
        // Disabling renames the jar to .jar.disabled and rebuilds the list — the second way the
        // updates were being thrown away.
        var path = Jar("sodium.jar");
        var mods = CheckedWith(Config(), path);

        mods.ToggleModCommand.Execute(mods.InstalledMods.Single());

        var row = mods.InstalledMods.Single();
        Assert.False(row.IsEnabled);
        Assert.True(row.UpdateAvailable);
    }

    [Fact]
    public void AJarReplacedByHandIsNotOfferedTheOldUpdate()
    {
        // The update was found for the file as the check saw it. Something else changed it since —
        // another version dropped in over the top — so the offer no longer describes this file.
        var path = Jar("lithium.jar", "v1");
        var mods = CheckedWith(Config(), path);

        File.WriteAllText(path, "otra version distinta y mas larga");
        mods.ReloadInstalled();

        Assert.Empty(WithUpdates(mods));
    }

    [Fact]
    public void MovingTheServerToAnotherVersionDropsEveryUpdate()
    {
        // A check answers "newest compatible with this server as it is". On another Minecraft
        // version every answer is for something else, and installing it is how a server ends up
        // with mods it cannot load.
        var config = Config();
        var mods = CheckedWith(config, Jar("a.jar"), Jar("b.jar"));

        config.GameVersion = "1.21.4";
        mods.ReloadInstalled();

        Assert.Empty(WithUpdates(mods));
        Assert.False(mods.HasPendingUpdates);
    }

    [Fact]
    public void ANewCheckReplacesWhatTheLastOneFound()
    {
        var a = Jar("a.jar");
        var b = Jar("b.jar");
        var mods = CheckedWith(Config(), a, b);

        mods.BeginPendingUpdates();
        mods.RememberUpdate(b, Newer("b.jar"));
        mods.ReloadInstalled();

        Assert.Equal(new[] { "b.jar" }, WithUpdates(mods));
    }

    [Fact]
    public void UpdateAllIsOfferedOnlyWhenThereIsSomethingToUpdate()
    {
        var path = Jar("a.jar");
        var mods = new ServerModsViewModel(Config());

        Assert.False(mods.UpdateAllCommand.CanExecute(null));

        mods.BeginPendingUpdates();
        mods.RememberUpdate(path, Newer("a.jar"));
        mods.ReloadInstalled();

        Assert.True(mods.UpdateAllCommand.CanExecute(null));
        Assert.Contains("1", mods.UpdateAllText);
    }

    // --- What "update all" says afterwards ---

    [Fact]
    public void UpdatingAllSaysHowManyWent()
    {
        var text = ServerModsViewModel.UpdateAllSummary(5, Array.Empty<string>(), stoppedByServer: false);

        Assert.Equal(string.Format(Localizer.Get("Msg_UpdateAllDoneFmt"), 5), text);
    }

    [Fact]
    public void APartialRunNamesWhatFailed()
    {
        // Naming them is what makes it something to act on: they stay in the list, and the user can
        // see which ones to look at.
        var text = ServerModsViewModel.UpdateAllSummary(3, new[] { "roto.jar", "otro.jar" }, stoppedByServer: false);

        Assert.Contains("roto.jar", text);
        Assert.Contains("otro.jar", text);
        Assert.DoesNotContain("{", text);
    }

    [Fact]
    public void ARunningServerStopsTheBatchAndSaysWhy()
    {
        // A jar in use means the server is running, and every other file is in use too. Carrying on
        // would only produce four more identical failures.
        var text = ServerModsViewModel.UpdateAllSummary(1, new[] { "a.jar" }, stoppedByServer: true);

        Assert.Equal(string.Format(Localizer.Get("Msg_UpdateAllStoppedFmt"), 1), text);
    }

    // --- The view ---

    [Fact]
    public void TheListIsOffWhileEverythingIsBeingUpdated()
    {
        // "Update all" walks these rows and swaps their files. A delete, a toggle or a single update
        // from the list at the same time would race it for the same folder.
        var list = Regex.Matches(Markup(), @"<ListBox\b[\s\S]*?>")
            .Select(m => m.Value)
            .Single(v => v.Contains("{Binding InstalledMods}", StringComparison.Ordinal));

        Assert.Contains("IsEnabled=\"{Binding !IsUpdatingAll}\"", list);
    }

    [Fact]
    public void EveryNameTheUpdateAllButtonBindsToExists()
    {
        // ServerModsView.axaml is x:CompileBindings="False": a mistyped name raises nothing and the
        // button simply never appears.
        var markup = Markup();
        var start = markup.IndexOf("x:Name=\"UpdateAllButton\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "el botón de actualizar todas ya no está en ServerModsView.axaml");

        start = markup.LastIndexOf("<Button", start, StringComparison.Ordinal);
        var end = markup.IndexOf("</Button>", start, StringComparison.Ordinal);

        foreach (Match m in Regex.Matches(markup[start..end], @"\{Binding ([A-Za-z0-9_]+)\}"))
            Assert.True(typeof(ServerModsViewModel).GetProperty(m.Groups[1].Value) is not null,
                $"ServerModsViewModel no tiene «{m.Groups[1].Value}», que el botón enlaza");
    }

    private static string Markup() => File.ReadAllText(Path.Combine(
        LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "ServerModsView.axaml"));
}
