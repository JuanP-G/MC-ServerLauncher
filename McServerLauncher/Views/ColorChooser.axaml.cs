using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>
/// A colour setting, edited by code or by eye: a swatch that opens the palette and the spectrum, and
/// the <c>#RRGGBB</c> box beside it.
/// </summary>
/// <remarks>
/// <see cref="Hex"/> is the one value; the box, the swatch and the chooser are three views of it. A
/// half-typed code (<c>#E0</c>) stays in the box without reaching <see cref="Hex"/>: the setting
/// behind it is only ever handed something drawable.
/// </remarks>
public partial class ColorChooser : UserControl
{
    public static readonly StyledProperty<string?> HexProperty =
        AvaloniaProperty.Register<ColorChooser, string?>(nameof(Hex), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The contrast the colour has to reach on the dark surfaces to be called readable.</summary>
    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<ColorChooser, double>(nameof(Threshold), ColorPalette.TextContrast);

    public string? Hex
    {
        get => GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    /// <summary>The colour the chooser was opened on, which Esc goes back to.</summary>
    private string? _before;

    /// <summary>Set while this control writes its own views, so their change events do not echo back.</summary>
    private bool _syncing;

    private readonly List<Button> _swatches = new();

    /// <summary>A flyout is not a control, so it gets no generated field; it is the opener's.</summary>
    private Flyout ChooserFlyout => (Flyout)Opener.Flyout!;

    public ColorChooser()
    {
        InitializeComponent();

        foreach (var hex in ColorPalette.Swatches)
        {
            var swatch = new Button
            {
                Tag = hex,
                Content = new Border { Background = new SolidColorBrush(Color.Parse(hex)) },
            };
            swatch.Classes.Add("swatch");
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) => Pick(hex);
            _swatches.Add(swatch);
            SwatchGrid.Children.Add(swatch);
        }

        HexBox.TextChanged += (_, _) => FromText(HexBox.Text);
        ChooserHexBox.TextChanged += (_, _) => FromText(ChooserHexBox.Text);
        Spectrum.ColorChanged += (_, e) => FromSpectrum(e.NewColor);
        HueSlider.ColorChanged += (_, e) => FromHue(e.NewColor);
        ChooserFlyout.Opened += (_, _) => Opened();
        Chooser.AddHandler(KeyDownEvent, OnChooserKeyDown, RoutingStrategies.Tunnel);
        Show();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HexProperty || change.Property == ThresholdProperty) Show();
    }

    /// <summary>A swatch was clicked.</summary>
    internal void Pick(string hex) => Hex = hex;

    /// <summary>What the chooser did when it opened: remember where it started.</summary>
    internal void Opened()
    {
        _before = Hex;
        Show();
    }

    /// <summary>Puts back the colour the chooser was opened on, and closes it.</summary>
    internal void Revert()
    {
        if (_before is not null) Hex = _before;
        ChooserFlyout.Hide();
    }

    private void OnChooserKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Revert();
        e.Handled = true;
    }

    private void FromText(string? text)
    {
        if (_syncing || ColorPalette.TryParse(text) is not { } color) return;
        Hex = ColorPalette.ToHex(color);
    }

    private void FromSpectrum(Color color)
    {
        if (_syncing) return;
        Hex = ColorPalette.ToHex(color);
    }

    /// <summary>The hue bar moved: same saturation and brightness as the square, the new hue.</summary>
    private void FromHue(Color hueColor)
    {
        if (_syncing) return;
        var square = Spectrum.HsvColor;
        var hue = hueColor.ToHsv().H;
        Hex = ColorPalette.ToHex(HsvColor.ToRgb(hue, square.S, square.V));
    }

    /// <summary>A brush from App.axaml, found from here once in the window and from the app before.</summary>
    private IBrush? Token(string key) =>
        this.FindResource(key) as IBrush ?? Application.Current?.FindResource(key) as IBrush;

    /// <summary>Brings the swatch, the boxes, the spectrum, the before/after and the contrast line to <see cref="Hex"/>.</summary>
    private void Show()
    {
        if (ColorPalette.TryParse(Hex) is not { } color) return;
        _syncing = true;
        try
        {
            var brush = new SolidColorBrush(color);
            OpenerSwatch.Background = brush;
            AfterSwatch.Background = brush;
            BeforeSwatch.Background = ColorPalette.TryParse(_before ?? Hex) is { } before ? new SolidColorBrush(before) : brush;

            var hex = ColorPalette.ToHex(color);
            if (!string.Equals(HexBox.Text, hex, StringComparison.OrdinalIgnoreCase) && !HexBox.IsFocused) HexBox.Text = hex;
            if (!string.Equals(ChooserHexBox.Text, hex, StringComparison.OrdinalIgnoreCase) && !ChooserHexBox.IsFocused) ChooserHexBox.Text = hex;

            // The square keeps its own hue while it is at grey or black, where the colour has none.
            if (ColorPalette.ToHex(Spectrum.Color) != hex) Spectrum.Color = color;
            HueSlider.HsvColor = new HsvColor(1, Spectrum.HsvColor.H, 1, 1);

            foreach (var swatch in _swatches)
                swatch.Classes.Set("on", string.Equals((string)swatch.Tag!, hex, StringComparison.OrdinalIgnoreCase));

            var readable = ColorPalette.ReadsWellOnDark(color, Threshold);
            ContrastText.Text = Localizer.Get(readable ? "Col_ReadsWell" : "Col_TooDark");
            ContrastText.Foreground = Token(readable ? "SemanticOk" : "SemanticWarn");
        }
        finally
        {
            _syncing = false;
        }
    }
}
