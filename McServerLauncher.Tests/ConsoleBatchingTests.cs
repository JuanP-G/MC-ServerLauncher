using System.Collections.Specialized;
using System.Reflection;
using Avalonia.Controls;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The console takes what a server prints in batches, not one trip to the UI thread per line.
/// </summary>
/// <remarks>
/// A modpack loading prints thousands of lines in a few seconds, and each used to be its own
/// dispatcher job and its own pair of collection notifications — which is what made the window
/// stutter while a big server started.
/// </remarks>
[Collection("avalonia")]
public class ConsoleBatchingTests(AvaloniaFixture ui)
{
    [Fact]
    public void AddRangeIsOneAddForAllTheItems()
    {
        var list = new BulkObservableCollection<int> { 1, 2 };
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.CollectionChanged += (_, e) => events.Add(e);

        list.AddRange(new[] { 3, 4, 5 });

        var added = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
        Assert.Equal(2, added.NewStartingIndex);
        Assert.Equal(new[] { 3, 4, 5 }, added.NewItems!.Cast<int>());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, list);
    }

    [Fact]
    public void AListShowsWhatWasAddedInOneGo()
    {
        ui.Run(() =>
        {
            var items = new BulkObservableCollection<string> { "a" };
            var listBox = new ListBox { ItemsSource = items };
            var window = new Window { Content = listBox, Width = 300, Height = 300 };
            window.Show();

            items.AddRange(new[] { "b", "c", "d" });
            AvaloniaFixture.Pump();

            Assert.Equal(4, listBox.ItemCount);
            window.Hide();
        });
    }

    [Fact]
    public void LinesFromTheServerArriveTogetherAndInOrder()
    {
        var feed = typeof(ServerViewModel).GetMethod("OnServerLine", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var folder = Path.Combine(Path.GetTempPath(), "mcl-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        ServerViewModel? server = null;
        var adds = 0;
        ui.Run(() =>
        {
            server = new ServerViewModel(new ServerConfig { Name = "survival", FolderPath = folder });
            server.ConsoleLines.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add) adds++;
            };

            // The UI thread is busy while the server prints, as it is while it lays out a frame:
            // everything printed meanwhile has to wait for one flush.
            Task.Run(() =>
            {
                for (var i = 0; i < 500; i++)
                    feed.Invoke(server, new object[] { $"[12:00:00] [Server thread/INFO]: line {i}", ConsoleSource.Stdout });
            }).Wait();
        });

        ui.Run(AvaloniaFixture.Pump);

        ui.Run(() =>
        {
            Assert.Equal(1, adds);
            Assert.Equal(500, server!.ConsoleLines.Count);
            Assert.EndsWith("line 0", server.ConsoleLines[0].Text);
            Assert.EndsWith("line 499", server.ConsoleLines[^1].Text);
        });

        try { Directory.Delete(folder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TheAppsOwnLinesAppearAtOnce()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcl-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        ui.Run(() =>
        {
            var server = new ServerViewModel(new ServerConfig { Name = "survival", FolderPath = folder });
            server.LogLauncher("hello");
            Assert.Equal("hello", Assert.Single(server.ConsoleLines).Text);   // no pump needed
        });

        try { Directory.Delete(folder, recursive: true); } catch { /* best-effort */ }
    }
}
