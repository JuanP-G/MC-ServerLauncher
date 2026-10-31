using Avalonia;
using Avalonia.Controls;

namespace McServerLauncher.Views;

/// <summary>
/// The button of a store project, bound to <see cref="ViewModels.InstallStateViewModel"/>: Install,
/// Update, Installed or Disabled, according to what is in the folder.
/// </summary>
public partial class InstallStateButton : UserControl
{
    /// <summary>The roomy version the details page uses, with its install button in the accent colour.</summary>
    public static readonly StyledProperty<bool> LargeProperty =
        AvaloniaProperty.Register<InstallStateButton, bool>(nameof(Large));

    public bool Large
    {
        get => GetValue(LargeProperty);
        set => SetValue(LargeProperty, value);
    }

    public InstallStateButton() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LargeProperty) return;

        var large = change.GetNewValue<bool>();
        Root.Classes.Set("large", large);
        InstallButton.Classes.Set("accent", large);
    }
}
