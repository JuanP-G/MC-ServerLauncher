using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using McServerLauncher.Localization;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _shuttingDown;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            // Warn about a corrupt servers.json first (rare), then the what's-new dialog.
            await _viewModel.WarnIfServersFileWasCorruptAsync(this);
            _viewModel.ShowWhatsNewIfUpdated(this);
        };

        // When switching servers, go back to the Console tab. Otherwise the previously selected tab
        // (e.g. Mods) could stay shown for a server that doesn't have it (a vanilla server).
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedServer))
                ServerTabs.SelectedIndex = 0;
        };

    }

    /// <summary>
    /// Optional minimize-to-tray (see settings): minimizing hides the window, so it leaves the taskbar
    /// and lives on only as the tray icon while the servers keep running. Also guarded by
    /// <see cref="App.TrayAvailable"/>: on a desktop with no tray there would be no way to bring the
    /// window back, so there minimize always keeps its normal behavior.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty || WindowState != WindowState.Minimized) return;
        if (!WindowBehavior.MinimizeToTray || !App.TrayAvailable) return;

        // Leave the minimized state behind BEFORE hiding, and never hide straight from it.
        //
        // On X11 an iconified window is already unmapped, so unmapping it again leaves the window
        // manager unable to tell "withdrawn" from "iconified": it keeps the taskbar button, and
        // mapping it again brings back a frame whose contents were never re-rendered — a window
        // that is nothing but its title bar. Both symptoms, one cause.
        //
        // The tell is that close-to-tray never had either problem: it hides from the normal state.
        // This makes minimize take exactly the same path.
        WindowState = WindowState.Normal;
        Hide();
    }

    /// <summary>
    /// Quits the app (stops servers and exits). Used by the tray's Exit menu, and the only way out
    /// when close-to-tray is enabled and the X no longer quits.
    /// </summary>
    public void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // Optional close-to-tray (see settings): the X hides the window instead of quitting, and the
        // tray's Exit menu (RequestExit) stays the way out. Same tray guard as minimize.
        if (!_exitRequested && WindowBehavior.CloseToTray && App.TrayAvailable)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Avoid closing abruptly with servers running: stop them cleanly first.
        if (!_shuttingDown)
        {
            e.Cancel = true;
            _shuttingDown = true;
            if (_viewModel.AnyServerRunning)
                Title = Localizer.Get("Msg_ClosingTitle");

            // We've already saved the config and stopped the servers in here.
            await _viewModel.ShutdownAllAsync();

            // Immediate exit: avoids the time the toolkit takes to release its resources on close.
            Environment.Exit(0);
            return;
        }

        base.OnClosing(e);
    }

    private void ConsoleCopy_Click(object? sender, RoutedEventArgs e) => _ = CopyConsole(selectedOnly: true);

    private void ConsoleCopyAll_Click(object? sender, RoutedEventArgs e) => _ = CopyConsole(selectedOnly: false);

    private void ConsoleList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopyConsole(selectedOnly: true);
            e.Handled = true;
        }
    }

    private async System.Threading.Tasks.Task CopyConsole(bool selectedOnly)
    {
        IList source = selectedOnly && ConsoleList.SelectedItems is { Count: > 0 }
            ? ConsoleList.SelectedItems
            : ConsoleList.Items;

        var lines = source.Cast<object?>().Select(o => o?.ToString() ?? string.Empty);
        var text = string.Join(Environment.NewLine, lines);
        if (!string.IsNullOrEmpty(text) && Clipboard is not null)
        {
            try { await Clipboard.SetTextAsync(text); } catch { /* clipboard busy */ }
        }
    }
}
