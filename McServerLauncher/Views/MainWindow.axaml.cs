using Avalonia;
using Avalonia.Controls;
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

        // Hide FIRST, and only then clear the minimized state — while the window is already gone.
        //
        // The order is the whole point, and getting it backwards is worse than either alternative.
        // Restoring the window before hiding it asks the window manager to map it and unmap it in
        // the same breath; those are asynchronous, so the map can land last and leave a mapped
        // window whose contents were never painted — a bare frame sitting on the desktop. Hiding
        // first issues exactly one operation to the window manager, so there is nothing to race.
        //
        // Clearing the state afterwards still matters: it is only ever read again by the next
        // Show(), and mapping a window that is still flagged minimized is what used to bring it
        // back empty. Setting it here costs nothing because an unmapped window has nothing to draw.
        Hide();
        WindowState = WindowState.Normal;
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

}
