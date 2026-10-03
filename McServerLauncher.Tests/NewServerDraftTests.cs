using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The new-server panel against the server list: what is selected while it is open, and what
/// happens to a half-filled one when another server is picked.
/// </summary>
/// <remarks>
/// A real <see cref="MainViewModel"/> over a temporary data folder, without <c>Activate()</c>, as in
/// <see cref="MainViewModelFlowTests"/>: nothing here reaches outside the app.
/// </remarks>
[Collection("avalonia")]
public class NewServerDraftTests : IDisposable
{
    private readonly AvaloniaFixture _ui;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mcl-draft-" + Guid.NewGuid().ToString("N"));

    public NewServerDraftTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_dataDir);
        var servers = new[] { "uno", "dos" }.Select(name => new ServerConfig
        {
            Id = name, Name = name, FolderPath = Directory.CreateDirectory(Path.Combine(_dataDir, name)).FullName,
            Type = ServerType.Vanilla, GameVersion = "1.21.1"
        });
        File.WriteAllText(Path.Combine(_dataDir, "servers.json"), JsonSerializer.Serialize(servers));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void OpeningThePanelLeavesNoServerLookingSelected() =>
        _ui.Run(() =>
        {
            // The previous server used to stay highlighted, as if it were the one on screen.
            var main = new MainViewModel(_dataDir);
            Assert.NotNull(main.SelectedServer);

            main.ShowNewServerCommand.Execute(null);

            Assert.Null(main.SelectedServer);
            Assert.True(main.IsCreatingServer);
            Assert.False(main.ShowServerDetail);
        });

    [Fact]
    public void PickingAServerShowsItAndKeepsTheUnfinishedOneAsADraft() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            main.ShowNewServerCommand.Execute(null);
            var draft = main.NewServerPanel;

            main.SelectedServer = main.Servers[1];

            Assert.False(main.IsCreatingServer);
            Assert.True(main.ShowServerDetail);
            Assert.True(main.HasNewServerDraft);

            main.ShowNewServerCommand.Execute(null);
            Assert.Same(draft, main.NewServerPanel);   // back where it was left
            Assert.False(main.HasNewServerDraft);
        });

    [Fact]
    public void CancellingGoesBackToTheServerThatWasOnScreen() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            main.SelectedServer = main.Servers[1];
            main.ShowNewServerCommand.Execute(null);

            main.NewServerPanel!.FindControl<Button>("CancelButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Null(main.NewServerPanel);
            Assert.False(main.IsCreatingServer);
            Assert.Same(main.Servers[1], main.SelectedServer);
        });
}
