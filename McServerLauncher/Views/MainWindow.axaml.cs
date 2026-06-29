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

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.ShowWhatsNewIfUpdated(this);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
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
