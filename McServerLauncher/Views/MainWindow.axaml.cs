using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Avalonia.Media.Transformation;
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

            if (e.PropertyName == nameof(MainViewModel.Section))
                PlaceRailPip();
        };

        // El panel de la pestaña que llega se relanza a mano: una animacion de Avalonia se dispara
        // cuando el selector empieza a encajar, y la clase ya esta puesta desde la vez anterior.
        ServerTabs.SelectionChanged += (_, _) => ReplayTabEnter();

        // Tambien al cambiar el tamaño: las filas del rail son Auto, asi que una etiqueta que se
        // parte en dos lineas mueve el segundo boton y con el, el destino de la marca.
        RailGrid.LayoutUpdated += (_, _) => PlaceRailPip();
    }

    /// <summary>
    /// Replays the "the panel arrived" animation on the tab that just became selected.
    /// </summary>
    /// <remarks>
    /// Removing the class and adding it back is not a trick, it is how Avalonia animations start:
    /// they run when the selector BEGINS to match. After the first tab change the class is already
    /// there, so adding it again does nothing at all — it has to stop matching first.
    ///
    /// The animation itself lives in <c>Styles/Motion.axaml</c> like every other one, so switching
    /// animations off leaves this method toggling a class that no style reacts to, and the tab
    /// simply appears. That is the correct behaviour, not a gap.
    /// </remarks>
    private void ReplayTabEnter()
    {
        var host = ServerTabs.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_SelectedContentHost");

        if (host is null) return;

        host.Classes.Remove("tabenter");
        host.Classes.Add("tabenter");
    }

    /// <summary>
    /// Puts the rail's "you are here" bar over the section currently showing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured rather than hardcoded, and that is the whole reason this is code and not XAML: the
    /// rail's rows are <c>Auto</c>, so a longer translation of a label wraps to two lines and makes
    /// one button taller than the other. Any fixed offset would be right in Spanish and wrong in
    /// German.
    /// </para>
    /// <para>
    /// This only sets the destination — the travelling is done by the transition on
    /// <c>Border.railpip</c> in <c>Styles/Motion.axaml</c>, because every animation in the app lives
    /// there and <c>MotionTests</c> fails the build if one appears anywhere else. It also means
    /// switching animations off stops the bar sliding and leaves it landing instantly, which is
    /// exactly what that setting promises.
    /// </para>
    /// <para>
    /// Called from <c>LayoutUpdated</c>, which fires constantly — hence the guard. Writing Height
    /// unconditionally would invalidate layout, which raises LayoutUpdated, which writes Height: a
    /// loop that pins a core at 100% and is invisible until somebody notices the fan.
    /// </para>
    /// </remarks>
    private void PlaceRailPip()
    {
        var target = _viewModel.IsTunnelsSection ? RailTunnels : RailServers;
        if (target.Bounds.Height <= 0) return;

        // Inset so it reads as a marker beside the button rather than a bar the same size as it.
        const double inset = 10;
        var height = Math.Max(1, target.Bounds.Height - inset * 2);
        var y = target.Bounds.Y + inset;

        // El NaN va primero, y no es paranoia: Height arranca en NaN ("auto"), y CUALQUIER
        // comparacion con NaN es falsa — Math.Abs(NaN - 32) > 0.5 da false. Con solo la resta, la
        // altura no se asignaba nunca y la barra no llegaba a dibujarse. En una captura eso se ve
        // como "no aparece" y se le echa la culpa al color, al Panel o al selector.
        if (double.IsNaN(RailPip.Height) || Math.Abs(RailPip.Height - height) > 0.5)
            RailPip.Height = height;

        var wanted = string.Create(CultureInfo.InvariantCulture, $"translateY({y:0.##}px)");
        if (RailPip.Tag as string != wanted)
        {
            RailPip.Tag = wanted;
            RailPip.RenderTransform = TransformOperations.Parse(wanted);
        }
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
