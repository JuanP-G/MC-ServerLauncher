using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using McServerLauncher.Localization;
using McServerLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace McServerLauncher.Views;

public partial class MainWindow : FluentWindow
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        // Evitamos cerrar de golpe con servidores corriendo: paramos limpiamente primero.
        if (!_shuttingDown)
        {
            e.Cancel = true;
            _shuttingDown = true;
            if (_viewModel.AnyServerRunning)
                Title = Localizer.Get("Msg_ClosingTitle");

            // Ya hemos guardado la config y detenido los servidores aquí dentro.
            await _viewModel.ShutdownAllAsync();

            // Salida inmediata: evita el ~2 s que tarda WPF/WPF-UI en liberar sus recursos al cerrar.
            Environment.Exit(0);
            return;
        }

        base.OnClosing(e);
    }

    private void ConsoleCopy_Click(object sender, RoutedEventArgs e) => CopyConsole(selectedOnly: true);

    private void ConsoleCopyAll_Click(object sender, RoutedEventArgs e) => CopyConsole(selectedOnly: false);

    private void ConsoleList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CopyConsole(selectedOnly: true);
            e.Handled = true;
        }
    }

    private void CopyConsole(bool selectedOnly)
    {
        var source = selectedOnly && ConsoleList.SelectedItems.Count > 0
            ? ConsoleList.SelectedItems
            : (System.Collections.IList)ConsoleList.Items;

        var lines = source.Cast<object?>().Select(o => o?.ToString() ?? string.Empty);
        var text = string.Join(Environment.NewLine, lines);
        if (!string.IsNullOrEmpty(text))
        {
            try { Clipboard.SetText(text); } catch { /* portapapeles ocupado */ }
        }
    }
}
