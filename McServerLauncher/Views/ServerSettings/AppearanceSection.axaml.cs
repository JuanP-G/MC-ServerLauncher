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

namespace McServerLauncher.Views.ServerSettings;

/// <summary>
/// Everything the server card shows: the image, the name and the two lines of the MOTD with colours
/// and formatting, next to a preview made by the same control the server's header uses.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is written here. The image the user picks is rendered into memory and handed to the
/// draft, the name and the MOTD go into it as they are typed, and the page writes all of it on Save
/// — so choosing an image and then discarding leaves the server exactly as it was.
/// </para>
/// <para>
/// The two text boxes hold plain text; the colours live in a <see cref="MotdDocument"/> that is kept
/// in step with every keystroke. The format bar and the colours are in
/// <c>AppearanceSection.Colors.cs</c>.
/// </para>
/// </remarks>
public partial class AppearanceSection : UserControl, IServerSettingsSection
{
    private const string DefaultMotd = "A Minecraft Server";

    private readonly ServerSettingsDraft _draft;
    private readonly ServerIconService _icons = new();
    private readonly bool _wake;
    private readonly string _maxPlayers;
    private readonly TextBox[] _boxes;

    private MotdDocument _doc = MotdDocument.FromProperties(DefaultMotd);

    // Style chosen from the toolbar with nothing selected: used by the next characters typed in that
    // line, then forgotten.
    private readonly MotdStyle?[] _pending = new MotdStyle?[2];
    private int _active;
    private bool _loading = true;

    private Bitmap? _icon;

    /// <summary>The draft this page writes into. For tests.</summary>
    internal ServerSettingsDraft Draft => _draft;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public AppearanceSection() : this(new ServerSettingsDraft(new ServerConfig()), false) { }

    public AppearanceSection(ServerSettingsDraft draft, bool isRunning)
    {
        InitializeComponent();
        _draft = draft;
        _boxes = [Line1Box, Line2Box];

        var maxPlayers = draft.FileValue("max-players");
        _maxPlayers = int.TryParse(maxPlayers, out var n) ? n.ToString() : "20";

        // What the file says, normalised through the document: that is what Save would write back
        // untouched, so it is what counts as unchanged.
        var raw = draft.FileValue("motd");
        _doc = MotdDocument.FromProperties(string.IsNullOrWhiteSpace(raw) ? DefaultMotd : raw);
        draft.Track("motd", _doc.ToProperties());
        ExtraLinesNote.IsVisible = _doc.HadExtraLines;
        RestartNote.IsVisible = isRunning;


        _wake = draft.Live.WakeOnDemand;
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
        NameBox.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _draft.Config.Name = NameBox.Text ?? string.Empty;
            Refresh();
        };

        SetUpColors();
        Reload();
    }

    /// <inheritdoc />
    public void Reload()
    {
        _loading = true;
        _doc = MotdDocument.FromProperties(_draft.Get("motd"));
        _pending[0] = _pending[1] = null;
        Line1Box.Text = _doc.GetText(0);
        Line2Box.Text = _doc.GetText(1);
        NameBox.Text = _draft.Config.Name;
        _icon = _draft.NewIcon is { } png ? Decode(png) : _draft.RemoveIcon ? null : LoadCurrentIcon();
        ImageError.IsVisible = false;
        _loading = false;
        Refresh();
        UpdateToolbar();
    }

    /// <inheritdoc />
    public void ShowError(string? message)
    {
        Error.Text = message;
        Error.IsVisible = message is not null;
    }

    // ---------------------------------------------------------------- preview

    private void Refresh()
    {
        var asleep = _wake && PreviewAsleep.IsChecked == true;
        var config = _draft.Live;

        // The sleeping preview goes through the same code the listener uses to answer the game, so
        // what is shown here cannot differ from what a player is sent.
        Preview.Motd = asleep
            ? WakeSign.Compose(_doc.ToProperties(), WakeSign.Notice(starting: false))
            : _doc.ToCodes();

        Line2Box.Opacity = asleep ? 0.55 : 1;
        Line2Note.IsVisible = asleep;

        Preview.ServerName = NameBox.Text;
        Preview.TypeText = config.Type.ToString();
        Preview.TypeBrush = ServerTypeBrushes.For(config.Type);
        Preview.Version = config.GameVersion;
        Preview.PlayerCount = "0/" + _maxPlayers;
        Preview.SignalBrush = new SolidColorBrush(Color.Parse("#55FF55"));
        Preview.Icon = _icon;

        IconImage.Source = _icon;
        IconPlaceholder.IsVisible = _icon is null;
        RemoveImageButton.IsEnabled = _icon is not null;
    }

    private Bitmap? LoadCurrentIcon()
    {
        var path = Path.Combine(_draft.Live.FolderPath, ServerIconService.FileName);
        if (!File.Exists(path)) return null;
        try
        {
            // Read fully into memory so the file is not locked while the page is open.
            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        }
        catch { return null; }
    }

    private static Bitmap Decode(byte[] png)
    {
        using var ms = new MemoryStream(png);
        return new Bitmap(ms);
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

        MotdChanged();
    }

    private void MotdChanged()
    {
        _draft.Set("motd", _doc.ToProperties());
        Refresh();
        UpdateToolbar();
    }

    // ---------------------------------------------------------------- image

    private async void ChangeImage_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
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
            ImageError.Text = string.Format(Localizer.Get("Msg_IconCreateError"), ex.Message);
            ImageError.IsVisible = true;
        }
    }

    /// <summary>Renders the image the way the server will store it, and previews that — not the original.</summary>
    internal void UseImage(string path)
    {
        var png = _icons.RenderIcon(path);
        _icon = Decode(png);
        ImageError.IsVisible = false;
        _draft.UseIcon(png);
        Refresh();
    }

    private void RemoveImage_Click(object? sender, RoutedEventArgs e)
    {
        _icon = null;
        _draft.ClearIcon();
        Refresh();
    }
}
