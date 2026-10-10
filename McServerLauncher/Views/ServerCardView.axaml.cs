using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace McServerLauncher.Views;

/// <summary>
/// The server as the game's server list draws it: icon, name, loader badge and version, the MOTD
/// in colour, the player count and the signal bars.
/// </summary>
/// <remarks>
/// Plain styled properties and nothing else, so the same control can show a live server (bound to
/// its view model) or the half-edited one on the Appearance settings page (set from code). It deliberately
/// knows nothing about either.
/// </remarks>
public partial class ServerCardView : UserControl
{
    public static readonly StyledProperty<Bitmap?> IconProperty =
        AvaloniaProperty.Register<ServerCardView, Bitmap?>(nameof(Icon));

    public static readonly StyledProperty<string?> ServerNameProperty =
        AvaloniaProperty.Register<ServerCardView, string?>(nameof(ServerName));

    public static readonly StyledProperty<string?> TypeTextProperty =
        AvaloniaProperty.Register<ServerCardView, string?>(nameof(TypeText));

    public static readonly StyledProperty<IBrush?> TypeBrushProperty =
        AvaloniaProperty.Register<ServerCardView, IBrush?>(nameof(TypeBrush));

    public static readonly StyledProperty<string?> VersionProperty =
        AvaloniaProperty.Register<ServerCardView, string?>(nameof(Version));

    /// <summary>The MOTD with its § codes, as the renderer takes it.</summary>
    public static readonly StyledProperty<string?> MotdProperty =
        AvaloniaProperty.Register<ServerCardView, string?>(nameof(Motd));

    public static readonly StyledProperty<string?> PlayerCountProperty =
        AvaloniaProperty.Register<ServerCardView, string?>(nameof(PlayerCount));

    public static readonly StyledProperty<IBrush?> SignalBrushProperty =
        AvaloniaProperty.Register<ServerCardView, IBrush?>(nameof(SignalBrush));

    public static readonly StyledProperty<string?> SignalHintProperty =
        AvaloniaProperty.Register<ServerCardView, string?>(nameof(SignalHint));

    /// <summary>Shows the pencil that opens the editor. Off in the preview, which is the editor.</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<ServerCardView, bool>(nameof(IsEditable));

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<ServerCardView, ICommand?>(nameof(EditCommand));

    public Bitmap? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? ServerName { get => GetValue(ServerNameProperty); set => SetValue(ServerNameProperty, value); }
    public string? TypeText { get => GetValue(TypeTextProperty); set => SetValue(TypeTextProperty, value); }
    public IBrush? TypeBrush { get => GetValue(TypeBrushProperty); set => SetValue(TypeBrushProperty, value); }
    public string? Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public string? Motd { get => GetValue(MotdProperty); set => SetValue(MotdProperty, value); }
    public string? PlayerCount { get => GetValue(PlayerCountProperty); set => SetValue(PlayerCountProperty, value); }
    public IBrush? SignalBrush { get => GetValue(SignalBrushProperty); set => SetValue(SignalBrushProperty, value); }
    public string? SignalHint { get => GetValue(SignalHintProperty); set => SetValue(SignalHintProperty, value); }
    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }
    public ICommand? EditCommand { get => GetValue(EditCommandProperty); set => SetValue(EditCommandProperty, value); }

    public ServerCardView()
    {
        InitializeComponent();
    }
}
