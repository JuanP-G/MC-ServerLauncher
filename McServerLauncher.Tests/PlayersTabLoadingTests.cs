using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The player list is built when the Players tab is shown, and never before.
/// </summary>
/// <remarks>
/// <para>
/// It used to be rebuilt from scratch on every join and every leave, for every running server,
/// whether or not that server was the one selected and whether or not anyone had ever opened the
/// tab: a copy of every record, a sort, and a notification per row. The cost only showed up as
/// "switching to a running server feels slow", because a running server is the only one that emits
/// joins and leaves.
/// </para>
/// <para>
/// So the rule is the one the Backups tab already follows: nothing is read until somebody looks.
/// These tests hold it, because the failure mode is invisible — a list that is empty because nobody
/// asked for it looks exactly like a history that recorded nothing.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class PlayersTabLoadingTests : IDisposable
{
    private readonly AvaloniaFixture _ui;

    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "mcl-players-tab-" + Guid.NewGuid().ToString("N"));

    public PlayersTabLoadingTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_dataDir);
    }

    /// <summary>A server folder whose usercache names one player the list would show.</summary>
    private ServerConfig ServerThatKnows(string player)
    {
        var folder = Path.Combine(_dataDir, "survival");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "usercache.json"),
            $$"""[{"name":"{{player}}","uuid":"11111111-2222-3333-4444-555555555555"}]""");
        return new ServerConfig { Name = "survival", FolderPath = folder };
    }

    [Fact]
    public void BuildingAServerDoesNotBuildItsPlayerList()
    {
        _ui.Run(() =>
        {
            var server = new ServerViewModel(ServerThatKnows("Alice"));

            // The names were read — the other lists need them — but nobody turned them into rows.
            Assert.Contains("Alice", server.KnownPlayers);
            Assert.Empty(server.History.Rows);
        });
    }

    [Fact]
    public void OpeningTheTabBuildsIt()
    {
        _ui.Run(() =>
        {
            var server = new ServerViewModel(ServerThatKnows("Alice"));

            server.History.EnsureLoaded();

            Assert.Equal(new[] { "Alice" }, server.History.Rows.Select(r => r.Name));
        });
    }

    [Fact]
    public void AskingTwiceCostsNothingTheSecondTime()
    {
        _ui.Run(() =>
        {
            var server = new ServerViewModel(ServerThatKnows("Alice"));
            server.History.EnsureLoaded();
            var first = server.History.Rows[0];

            server.History.EnsureLoaded();

            // Same row object: the second call did not rebuild the list behind it.
            Assert.Same(first, server.History.Rows[0]);
        });
    }

    [Fact]
    public void ARequestedRebuildIsNeverDoneOnTheSpot()
    {
        _ui.Run(() =>
        {
            var server = new ServerViewModel(ServerThatKnows("Alice"));
            server.History.EnsureLoaded();
            var before = server.History.Rows[0];

            // Ten players arriving at once is the case this protects: each one used to pay for a
            // full rebuild, on the UI thread, there and then.
            for (var i = 0; i < 10; i++) server.History.RequestRefresh();

            Assert.Same(before, server.History.Rows[0]);
            server.History.Shutdown();
        });
    }

    [Fact]
    public void RequestingOneBeforeTheTabIsOpenedBuildsNothing()
    {
        _ui.Run(() =>
        {
            var server = new ServerViewModel(ServerThatKnows("Alice"));

            server.History.RequestRefresh();

            Assert.Empty(server.History.Rows);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }
}
