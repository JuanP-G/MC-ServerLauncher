using System.Collections;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using McServerLauncher.Localization;
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
    /// Really quit the app (stops servers and exits). Called from the tray's Exit menu; closing the
    /// window with the X button only hides it to the tray (see <see cref="OnClosing"/>).
    /// </summary>
    public void RequestExit()
    {
        _exitRequested = true;
        Show();          // make sure the window exists/isn't hidden so Close() runs its handler
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // VPN-style behavior: clicking the X doesn't quit, it hides the window to the tray (removing it
        // from the taskbar) and keeps the servers running. Exit only happens from the tray's Exit menu.
        if (!_exitRequested)
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
