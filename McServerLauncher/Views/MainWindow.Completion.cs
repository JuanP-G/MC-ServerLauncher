using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>
/// The keys of the console's completion, and the greyed rest drawn over the box. What the keys do
/// is the view model's (<see cref="ServerViewModel.AcceptSuggestion"/> and its neighbours); this
/// only gets the keys to it and keeps the drawing in step with the box.
/// </summary>
public partial class MainWindow
{
    private ServerViewModel? _completionServer;

    private void InitCommandCompletion()
    {
        // Tunnel, not bubble: Tab never reaches a TextBox's own handlers, focus navigation takes it
        // first, and the arrows would move the caret before anything here saw them.
        CommandBox.AddHandler(KeyDownEvent, OnCommandKeyDown, RoutingStrategies.Tunnel);
        CommandBox.DataContextChanged += (_, _) => FollowCompletionServer(CommandBox.DataContext as ServerViewModel);
        CommandBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty || e.Property == BoundsProperty) PlaceGhost();
        };
        CommandBox.TemplateApplied += (_, _) => Dispatcher.UIThread.Post(PlaceGhost);
        FollowCompletionServer(CommandBox.DataContext as ServerViewModel);
    }

    private void FollowCompletionServer(ServerViewModel? server)
    {
        if (ReferenceEquals(server, _completionServer)) return;
        if (_completionServer is not null) _completionServer.CommandCompleted -= OnCommandCompleted;
        _completionServer = server;
        if (server is not null) server.CommandCompleted += OnCommandCompleted;
    }

    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (CommandBox.DataContext is not ServerViewModel vm) return;
        switch (e.Key)
        {
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                // Like any console: Tab completes, and with nothing to complete it does nothing
                // rather than throwing the focus somewhere else mid-command.
                vm.AcceptSuggestion();
                e.Handled = true;
                break;
            case Key.Up:
                e.Handled = vm.MoveSuggestion(-1);
                if (e.Handled) KeepSelectedInView(vm);
                break;
            case Key.Down:
                e.Handled = vm.MoveSuggestion(1);
                if (e.Handled) KeepSelectedInView(vm);
                break;
            case Key.Escape:
                e.Handled = vm.DismissSuggestions();
                break;
        }
    }

    private void OnCommandCompleted()
    {
        // After the binding has put the new text in the box.
        Dispatcher.UIThread.Post(() =>
        {
            CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
            CommandBox.Focus();
        });
    }

    private void Suggestion_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SuggestionItem item } && CommandBox.DataContext is ServerViewModel vm)
            vm.PickSuggestion(item);
    }

    private void KeepSelectedInView(ServerViewModel vm)
    {
        var index = vm.SelectedSuggestion;
        Dispatcher.UIThread.Post(() =>
        {
            if (SuggestionsList.ContainerFromIndex(index) is { } row) row.BringIntoView();
        });
    }

    /// <summary>
    /// Lines the greyed rest up with the text in the box, and hides it whenever it could not line
    /// up: the caret is not at the end, or the text has scrolled sideways.
    /// </summary>
    private void PlaceGhost()
    {
        var presenter = CommandBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        var scroller = CommandBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (presenter is null) return;

        var origin = presenter.TranslatePoint(new Point(0, 0), CommandGhostText.Parent as Visual ?? CommandBox);
        if (origin is { } at)
            CommandGhostText.Margin = new Thickness(at.X, 0, 0, 0);

        var atEnd = CommandBox.CaretIndex >= (CommandBox.Text?.Length ?? 0);
        var scrolled = scroller is { Offset.X: > 0.5 };
        CommandGhostText.Opacity = atEnd && !scrolled ? 1 : 0;
    }
}
