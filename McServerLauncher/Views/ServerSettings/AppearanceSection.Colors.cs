using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>The format bar and the colours of the MOTD editor.</summary>
/// <remarks>
/// <para>
/// The bar acts on the selection of whichever line was last focused, and with nothing selected it
/// sets the style the next typed characters take. It also reads back: the colour button shows the
/// colour at the caret (or the selection's first character) and the format buttons are pressed when
/// the text there has them, so it answers what the text is as well as changing it.
/// </para>
/// <para>
/// The colours open inside the card, not in a popup: the 16 codes by name in two rows (the dark ones
/// over their light pair, as the game lists them), the last ones used, a ramp between two colours,
/// and a free RGB colour where the server can show one (<see cref="MotdDocument.HexWorksOn"/>).
/// Without RGB the ramp falls back to the nearest of the 16, in bands.
/// </para>
/// <para>
/// The hex box is the one control here that takes the focus, and taking it clears the line's
/// selection. So the selection is taken as the pointer goes down on the box — before the focus
/// moves — and the box acts on that.
/// </para>
/// </remarks>
public partial class AppearanceSection
{
    private const int MaxRecent = 8;

    // Shared by every server's page for as long as the app runs: "the gold I just used" is the
    // person's, not the server's.
    private static readonly List<MotdStyle> Recent = new();

    private (int Line, int Start, int Length)? _hexSelection;
    private bool _hexOk;
    private bool _gradient;
    private bool _pickingTo;
    private int _gradientFrom = MotdDocument.RgbOf('c');
    private int _gradientTo = MotdDocument.RgbOf('e');

    private void SetUpColors()
    {
        var live = _draft.Live;
        _hexOk = MotdDocument.HexWorksOn(live.Type, live.GameVersion);
        HexRow.IsVisible = _hexOk;
        HexNote.IsVisible = !_hexOk;

        BuildSwatches();
        HexBox.TextChanged += (_, _) => { HexError.IsVisible = false; ShowHexPreview(); };
        HexBox.AddHandler(PointerPressedEvent, (_, _) => _hexSelection ??= Selection(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        HexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            HexApply_Click(null, new RoutedEventArgs());
            e.Handled = true;
        };

        for (var line = 0; line < _boxes.Length; line++)
        {
            var l = line;
            _boxes[l].PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBox.SelectionStartProperty && e.Property != TextBox.SelectionEndProperty
                    && e.Property != TextBox.CaretIndexProperty) return;
                // Back in the text: whatever was taken for the hex box is over.
                if (_boxes[l].IsFocused) _hexSelection = null;
                if (!_loading) UpdateToolbar();
            };
        }

        SetMode(gradient: false);
        ShowRecent();
        ShowHexPreview();
    }

    // ---------------------------------------------------------------- reading the text back

    /// <summary>The line being edited and the selected range in it; length 0 means nothing is selected.</summary>
    private (int Line, int Start, int Length) Selection()
    {
        var box = _boxes[_active];
        var start = Math.Min(box.SelectionStart, box.SelectionEnd);
        return (_active, start, Math.Abs(box.SelectionEnd - box.SelectionStart));
    }

    /// <summary>The style new text would take at the caret if nothing had been chosen.</summary>
    private MotdStyle StyleAtCaret(int line)
    {
        var caret = Math.Min(_boxes[line].CaretIndex, _doc.Length(line));
        return _pending[line] ?? (caret > 0 ? _doc.StyleAt(line, caret - 1) : MotdStyle.Plain);
    }

    /// <summary>What the bar describes: the selection's first character, or the caret's style.</summary>
    private MotdStyle CurrentStyle()
    {
        var (line, start, length) = Selection();
        return length > 0 ? _doc.StyleAt(line, start) : StyleAtCaret(line);
    }

    private void UpdateToolbar()
    {
        var style = CurrentStyle();
        ColorButtonSwatch.Background = new SolidColorBrush(ColorOf(style));
        ColorButtonText.Text = NameOf(style);

        BoldButton.IsChecked = style.Format.HasFlag(MotdFormat.Bold);
        ItalicButton.IsChecked = style.Format.HasFlag(MotdFormat.Italic);
        UnderlineButton.IsChecked = style.Format.HasFlag(MotdFormat.Underline);
        StrikeButton.IsChecked = style.Format.HasFlag(MotdFormat.Strike);

        foreach (var swatch in ColorPanel.Children.OfType<Button>())
        {
            var code = (char)swatch.Tag!;
            var on = _gradient
                ? (_pickingTo ? _gradientTo : _gradientFrom) == MotdDocument.RgbOf(code)
                : !style.IsHex && style.Color == code;
            swatch.Classes.Set("on", on);
        }

        HexWarn.IsVisible = !_hexOk && _doc.HasHex;
        ColorHint.Text = _gradient
            ? Localizer.Get(_pickingTo ? "Look_GradientPickTo" : "Look_GradientPickFrom")
            : string.Format(Localizer.Get("Look_ColorHintFmt"), NameOf(style));
    }

    // ---------------------------------------------------------------- swatches

    private void BuildSwatches()
    {
        foreach (var code in MotdDocument.ColorCodes)
        {
            var button = Swatch(new SolidColorBrush(ColorOf(new MotdStyle(code, MotdFormat.None))));
            button.Tag = code;
            ToolTip.SetTip(button, Localizer.Get("MotdColor_" + code) + "  ·  §" + code);
            button.Click += Color_Click;
            ColorPanel.Children.Add(button);
        }
    }

    /// <summary>A Border inside a transparent Button: a Background set on the Button itself is
    /// replaced by the theme's hover colour, and the swatch would stop being its colour.</summary>
    private Button Swatch(IBrush fill, bool small = false)
    {
        var inner = new Border { CornerRadius = new CornerRadius(small ? 4 : 5), Background = fill, BorderThickness = new Thickness(1) };
        // Bound rather than looked up: the page is made before it is in the window, and a resource
        // looked up then is not found.
        inner.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SurfaceEdgeStrong"));
        var button = new Button { Classes = { "swatch" }, Content = inner, Focusable = false };
        if (small) button.Classes.Add("small");
        return button;
    }

    private void ShowRecent()
    {
        RecentPanel.Children.Clear();
        foreach (var style in Recent.Where(s => _hexOk || !s.IsHex))
        {
            var button = Swatch(new SolidColorBrush(ColorOf(style)), small: true);
            ToolTip.SetTip(button, NameOf(style));
            var picked = style;
            button.Click += (_, _) => UseColor(picked);
            RecentPanel.Children.Add(button);
        }
        RecentRow.IsVisible = RecentPanel.Children.Count > 0;
    }

    private static void Remember(MotdStyle color)
    {
        color = color with { Format = MotdFormat.None };
        Recent.Remove(color);
        Recent.Insert(0, color);
        if (Recent.Count > MaxRecent) Recent.RemoveAt(Recent.Count - 1);
    }

    // ---------------------------------------------------------------- solid colours

    private void Color_Click(object? sender, RoutedEventArgs e)
    {
        var code = (char)((Button)sender!).Tag!;
        if (_gradient)
        {
            SetGradientEnd(MotdDocument.RgbOf(code));
            return;
        }
        UseColor(new MotdStyle(code, MotdFormat.None));
    }

    /// <summary>Colours the selection, or sets the colour the next typed characters take.</summary>
    private void UseColor(MotdStyle color, (int Line, int Start, int Length)? range = null)
    {
        var (line, start, length) = range ?? Selection();
        if (length > 0)
        {
            if (color.IsHex) _doc.SetHex(line, start, length, color.Rgb);
            else _doc.SetColor(line, start, length, color.Color);
        }
        else _pending[line] = StyleAtCaret(line).WithColor(color.Color, color.Rgb);

        Remember(color);
        ShowRecent();
        MotdChanged();
    }

    private void HexApply_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryParseHex(HexBox.Text, out var rgb))
        {
            HexError.IsVisible = true;
            return;
        }
        if (_gradient) SetGradientEnd(rgb);
        else UseColor(MotdStyle.OfHex(rgb), _hexSelection);
    }

    private void ShowHexPreview() =>
        HexPreview.Background = TryParseHex(HexBox.Text, out var rgb)
            ? new SolidColorBrush(ColorOf(MotdStyle.OfHex(rgb)))
            : Brushes.Transparent;

    internal static bool TryParseHex(string? text, out int rgb)
    {
        rgb = 0;
        var t = text?.Trim().TrimStart('#') ?? "";
        if (t.Length == 3) t = string.Concat(t.Select(c => $"{c}{c}"));
        return t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    // ---------------------------------------------------------------- the ramp

    private void SolidMode_Click(object? sender, RoutedEventArgs e) => SetMode(gradient: false);

    private void GradientMode_Click(object? sender, RoutedEventArgs e) => SetMode(gradient: true);

    private void SetMode(bool gradient)
    {
        _gradient = gradient;
        _pickingTo = false;
        SolidMode.IsChecked = !gradient;
        GradientMode.IsChecked = gradient;
        GradientRow.IsVisible = gradient;
        RecentRow.IsVisible = !gradient && RecentPanel.Children.Count > 0;
        GradientHint.Text = Localizer.Get(_hexOk ? "Look_GradientHint" : "Look_GradientBanded");
        ShowGradient();
        UpdateToolbar();
    }

    private void GradientEnd_Click(object? sender, RoutedEventArgs e)
    {
        _pickingTo = (string?)((Button)sender!).Tag == "to";
        ShowGradient();
        UpdateToolbar();
    }

    private void SetGradientEnd(int rgb)
    {
        if (_pickingTo) _gradientTo = rgb;
        else _gradientFrom = rgb;
        // Picking the start moves on to the end, which is what comes next almost every time.
        _pickingTo = !_pickingTo;
        ShowGradient();
        UpdateToolbar();
    }

    private void ShowGradient()
    {
        GradientFromButton.Content = Swatch(new SolidColorBrush(ColorOf(MotdStyle.OfHex(_gradientFrom)))).Content;
        GradientToButton.Content = Swatch(new SolidColorBrush(ColorOf(MotdStyle.OfHex(_gradientTo)))).Content;
        GradientFromButton.Classes.Set("armed", !_pickingTo);
        GradientToButton.Classes.Set("armed", _pickingTo);

        // The bar shows what will be written: smooth with RGB, in bands without it.
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        const int steps = 12;
        for (var i = 0; i < steps; i++)
        {
            var t = i / (double)(steps - 1);
            var rgb = LerpRgb(_gradientFrom, _gradientTo, t);
            if (!_hexOk) rgb = MotdDocument.RgbOf(MotdDocument.NearestCode(rgb));
            brush.GradientStops.Add(new GradientStop(ColorOf(MotdStyle.OfHex(rgb)), t));
        }
        GradientBar.Background = brush;
    }

    /// <summary>The ramp across the selection, or across the whole line when nothing is selected.</summary>
    private void ApplyGradient_Click(object? sender, RoutedEventArgs e)
    {
        var (line, start, length) = Selection();
        if (length == 0) (start, length) = (0, _doc.Length(line));
        if (length == 0) return;

        _doc.SetGradient(line, start, length, _gradientFrom, _gradientTo, hex: _hexOk);
        MotdChanged();
    }

    private static int LerpRgb(int a, int b, double t)
    {
        int Channel(int shift) => (int)Math.Round(((a >> shift) & 255) + (((b >> shift) & 255) - ((a >> shift) & 255)) * t);
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    // ---------------------------------------------------------------- formats

    private void Format_Click(object? sender, RoutedEventArgs e)
    {
        var flag = Enum.Parse<MotdFormat>((string)((Control)sender!).Tag!);
        var (line, start, length) = Selection();

        if (length > 0)
        {
            // All of it has the format → take it off; otherwise put it on all of it.
            _doc.SetFormat(line, start, length, flag, on: !_doc.AllHave(line, start, length, flag));
        }
        else
        {
            var now = StyleAtCaret(line);
            _pending[line] = now with { Format = now.Format ^ flag };
        }

        MotdChanged();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        var (line, start, length) = Selection();

        if (length > 0) _doc.ClearStyle(line, start, length);
        else _pending[line] = MotdStyle.Plain;

        MotdChanged();
    }

    // ---------------------------------------------------------------- names and colours

    private static Color ColorOf(MotdStyle style) =>
        Color.FromUInt32(0xFF000000 | (uint)(style.IsHex ? style.Rgb
            : style.Color == '\0' ? MotdDocument.RgbOf('7') : MotdDocument.RgbOf(style.Color)));

    private static string NameOf(MotdStyle style) =>
        style.IsHex ? "#" + style.Rgb.ToString("X6")
        : style.Color == '\0' ? Localizer.Get("MotdColor_Default")
        : Localizer.Get("MotdColor_" + style.Color);
}
