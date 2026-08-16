using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

public partial class ServerModsView : UserControl
{
    public ServerModsView()
    {
        InitializeComponent();
        // Browse the top mods for this loader+version as soon as the view appears,
        // so results show up without the user having to search first.
        Loaded += (_, _) => (DataContext as ServerModsViewModel)?.EnsureLoaded();
    }

    /// <summary>
    /// Opens the details page for the card that was double-clicked. Bound from the template because
    /// the card is a Border, not a Button: making the whole card a button would swallow the Install
    /// and Details buttons inside it.
    /// </summary>
    private void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ModrinthProjectViewModel project }) return;
        if (project.OpenCommand.CanExecute(null)) project.OpenCommand.Execute(null);
        e.Handled = true;
    }
}
