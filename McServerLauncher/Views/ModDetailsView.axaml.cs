using Avalonia.Controls;

namespace McServerLauncher.Views;

public partial class ModDetailsView : UserControl
{
    public ModDetailsView()
    {
        InitializeComponent();

        // Opening a related mod (or going back) reuses this control with a new view model. Without
        // this, the new page would open at the scroll position the previous one was left at, which
        // looks like the page loaded halfway through.
        DataContextChanged += (_, _) => Scroller.Offset = default;
    }
}
