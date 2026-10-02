using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using McServerLauncher.Behaviors;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>
/// Everything the server card shows, in one window: the image, the name and the two lines of the
/// MOTD with colours and formatting, next to a preview made by the same control the main window uses.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is written until Save. The image the user picks is rendered into memory and previewed
/// from there, so choosing one and then cancelling leaves the server exactly as it was.
/// </para>
/// <para>
/// The two text boxes hold plain text; the colours live in a <see cref="MotdDocument"/> that is kept
/// in step with every keystroke. The toolbar acts on the selection of whichever box was last
/// focused, and with nothing selected it sets the style the next typed characters take.
/// </para>
/// </remarks>
public partial class ServerAppearanceDialog : Window
{
    private readonly ServerConfig _config;
    private readonly ServerPropertiesService _props = new();
    private readonly ServerIconService _icons = new();
    private readonly MotdDocument _doc;
    private readonly string _originalMotd;
    private readonly bool _wake;
    private readonly string _maxPlayers;
    private readonly TextBox[] _boxes;

    // Style chosen from the toolbar with nothing selected: used by the next characters typed in that
    // line, then forgotten.
    private readonly MotdStyle?[] _pending = new MotdStyle?[2];
    private int _active;
    private bool _loading = true;

    private Bitmap? _icon;
    private byte[]? _newIcon;
    private bool _removeIcon;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public ServerAppearanceDialog() : this(new ServerConfig(), false) { }

    public ServerAppearanceDialog(ServerConfig config, bool isRunning)
    {
        InitializeComponent();
        _config = config;
        _boxes = [Line1Box, Line2Box];

        var props = _props.Read(config.PropertiesPath);
        var raw = props.TryGetValue("motd", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "A Minecraft Server";
        _maxPlayers = props.TryGetValue("max-players", out var mp) && int.TryParse(mp, out var n) ? n.ToString() : "20";

        _doc = MotdDocument.FromProperties(raw);
        _originalMotd = _doc.ToProperties();

        Line1Box.Text = _doc.GetText(0);
        Line2Box.Text = _doc.GetText(1);
        NameBox.Text = config.Name;
        ExtraLinesNote.IsVisible = _doc.HadExtraLines;
        RestartNote.IsVisible = isRunning;

        _icon = LoadCurrentIcon();
        BuildSwatches();

        _wake = config.WakeOnDemand;
        PreviewModeRow.IsVisible = _wake;
        PreviewAsleep.IsChecked = _wake && !isRunning;
        PreviewAwake.IsChecked = _wake && isRunning;
        PreviewAsleep.IsCheckedChanged += (_, _) => Refresh();

        for (var line = 0; line < _boxes.Length; line++)
        {
            var l = line;
            _boxes[l].TextChanged += (_, _) => OnTextChanged(l);
            _boxes[l].GotFocus += (_, _) => _active = l;
        }
        NameBox.TextChanged += (_, _) => { if (!_loading) Refresh(); };

        _loading = false;
        Refresh();
    }

    // ---------------------------------------------------------------- preview

    private void Refresh()
    {
        var asleep = _wake && PreviewAsleep.IsChecked == true;

        // The sleeping preview goes through the same code the listener uses to answer the game, so
        // what is shown here cannot differ from what a player is sent.
        Preview.Motd = asleep
            ? WakeSign.Compose(_doc.ToProperties(), WakeSign.Notice(starting: false))
            : _doc.ToCodes();

        Line2Box.Opacity = asleep ? 0.55 : 1;
        Line2Note.IsVisible = asleep;

        Preview.ServerName = NameBox.Text;
        Preview.TypeText = _config.Type.ToString();
        Preview.TypeBrush = ServerTypeBrushes.For(_config.Type);
        Preview.Version = _config.GameVersion;
        Preview.PlayerCount = "0/" + _maxPlayers;
        Preview.SignalBrush = new SolidColorBrush(Color.Parse("#55FF55"));
        Preview.Icon = _icon;

        IconImage.Source = _icon;
        IconPlaceholder.IsVisible = _icon is null;
        RemoveImageButton.IsEnabled = _icon is not null;
    }

    private Bitmap? LoadCurrentIcon()
    {
        var path = Path.Combine(_config.FolderPath, ServerIconService.FileName);
        if (!File.Exists(path)) return null;
        try
        {
            // Read fully into memory so the file is not locked while the dialog is open.
            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- text

    private void OnTextChanged(int line)
    {
        if (_loading) return;

        var box = _boxes[line];
        _doc.SetText(line, box.Text, box.CaretIndex, _pending[line]);
        _pending[line] = null;

        // The document drops what a MOTD cannot hold (a stray §, a line break). Show the box what
        // was kept, so it never disagrees with the preview.
        var kept = _doc.GetText(line);
        if (box.Text != kept)
        {
            var caret = Math.Min(box.CaretIndex, kept.Length);
            _loading = true;
            box.Text = kept;
            box.CaretIndex = caret;
            _loading = false;
        }

        Refresh();
    }

    // ---------------------------------------------------------------- toolbar

    private void BuildSwatches()
    {
        var edge = (IBrush?)this.FindResource("SurfaceEdgeStrong");

        foreach (var code in MotdDocument.ColorCodes)
        {
            // A Border inside a transparent Button: a Background set on the Button itself is
            // replaced by the theme's hover colour, and the swatch would stop being its colour.
            var swatch = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(MinecraftMotd.Palette[code]),
                BorderBrush = edge, BorderThickness = new Thickness(1),
            };
            var button = new Button
            {
                Content = swatch, Padding = new Thickness(2), Background = Brushes.Transparent,
                Focusable = false, Tag = code,
                [ToolTip.TipProperty] = "§" + code,
            };
            button.Click += Color_Click;
            ColorPanel.Children.Add(button);
        }
    }

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

    private void Color_Click(object? sender, RoutedEventArgs e)
    {
        var color = (char)((Button)sender!).Tag!;
        var (line, start, length) = Selection();

        if (length > 0) _doc.SetColor(line, start, length, color);
        else _pending[line] = StyleAtCaret(line) with { Color = color };

        Refresh();
    }

    private void Format_Click(object? sender, RoutedEventArgs e)
    {
        var flag = Enum.Parse<MotdFormat>((string)((Button)sender!).Tag!);
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

        Refresh();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        var (line, start, length) = Selection();

        if (length > 0) _doc.ClearStyle(line, start, length);
        else _pending[line] = MotdStyle.Plain;

        Refresh();
    }

    // ---------------------------------------------------------------- image

    private async void ChangeImage_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectImage"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Get("Title_SelectImage"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif"]
                }
            ]
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        try { UseImage(path); }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(
                string.Format(Localizer.Get("Msg_IconCreateError"), ex.Message),
                Localizer.Get("Title_ChangeIcon"), this);
        }
    }

    /// <summary>Renders the image the way the server will store it, and previews that — not the original.</summary>
    internal void UseImage(string path)
    {
        var png = _icons.RenderIcon(path);
        using var ms = new MemoryStream(png);
        _icon = new Bitmap(ms);
        _newIcon = png;
        _removeIcon = false;
        Refresh();
    }

    private void RemoveImage_Click(object? sender, RoutedEventArgs e)
    {
        _icon = null;
        _newIcon = null;
        _removeIcon = true;
        Refresh();
    }

    // ---------------------------------------------------------------- save

    /// <summary>Writes what changed: the MOTD, the icon, and the name on the config. Returns the error text on failure.</summary>
    internal string? TrySave()
    {
        try
        {
            // Only when it actually changed: rewriting an untouched value would reformat a MOTD the
            // user wrote by hand, and would create server.properties on a server that never ran.
            var motd = _doc.ToProperties();
            if (motd != _originalMotd)
                _props.Update(_config.PropertiesPath, new Dictionary<string, string> { ["motd"] = motd });

            if (_newIcon is not null) _icons.WriteIcon(_config.FolderPath, _newIcon);
            else if (_removeIcon) _icons.RemoveIcon(_config.FolderPath);

            var name = NameBox.Text?.Trim();
            if (!string.IsNullOrEmpty(name)) _config.Name = name;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var error = TrySave();
        if (error is null) { Close(true); return; }

        await MessageBox.ShowAsync(
            string.Format(Localizer.Get("Msg_ConfigSaveError"), error),
            Localizer.Get("Look_Title"), this);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
