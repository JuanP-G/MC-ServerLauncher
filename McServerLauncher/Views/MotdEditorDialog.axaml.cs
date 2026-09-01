using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using McServerLauncher.Behaviors;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>
/// Edits the server's sign without ever asking anyone to learn a <c>§</c> code.
/// </summary>
/// <remarks>
/// <para>
/// Three views of one thing. The card at the top is the truth — it draws through
/// <see cref="MinecraftMotd"/>, the same renderer as the server header, so it cannot promise
/// something the game will not show. The middle box has the words with no codes in them. The
/// expander at the bottom has the raw sign, and pasting somebody else's into it works with no
/// special handling because that box <i>is</i> the document.
/// </para>
/// <para>
/// All three edit one <see cref="MotdDocument"/>. There is no second model, which is what keeps
/// this cheap and what stops the preview and the code drifting apart.
/// </para>
/// </remarks>
public partial class MotdEditorDialog : Window, INotifyPropertyChanged
{
    /// <summary>Minecraft's sixteen, in the order the game lists them.</summary>
    private const string Codes = "0123456789abcdef";

    private IReadOnlyList<MotdRun> _runs = Array.Empty<MotdRun>();

    /// <summary>Set while a box is being refreshed from the document, so it does not react to itself.</summary>
    private bool _syncing;

    /// <summary>The sign as it will be written to <c>server.properties</c>, once saved.</summary>
    public string Result { get; private set; } = string.Empty;

    /// <summary>
    /// The server's name as it was left here, once saved. Empty or unchanged means leave it alone.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Result"/> because they are written to different places: the sign
    /// goes into <c>server.properties</c> and the name into the app's own list. The dialog does not
    /// write either one — it says what was decided and lets the caller do it, which is why it works
    /// the same whether it was opened over a server or over nothing.
    /// </remarks>
    public string ResultName { get; private set; } = string.Empty;

    // What the preview card shows around the sign. Plain properties rather than a view model: the
    // dialog needs four values and none of them change while it is open.
    public string ServerName { get; }
    public string PlayersText { get; }
    public Bitmap? ServerIcon { get; private set; }
    public bool HasIcon => ServerIcon is not null;

    /// <summary>The server's folder, so the icon can be written into it. Null disables that.</summary>
    private readonly string? _folder;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public MotdEditorDialog() : this(string.Empty, "Servidor", "0/20", null) { }

    public MotdEditorDialog(string? motd, string serverName, string playersText, Bitmap? icon,
        string? serverFolder = null)
    {
        InitializeComponent();

        ServerName = serverName;
        PlayersText = playersText;
        ServerIcon = icon;
        _folder = serverFolder;
        DataContext = this;

        BuildSwatches();

        _runs = MotdDocument.Parse(motd);
        NameBox.Text = serverName;
        PreviewName.Text = serverName;
        RefreshAll();

        PlainBox.TextChanged += OnPlainChanged;
        CodeBox.TextChanged += OnCodeChanged;
        // La vista previa es la razon de ser de este dialogo, asi que el nombre tambien se ve
        // mientras se escribe. Sin enlace: el texto de la tarjeta ES el de la caja.
        NameBox.TextChanged += (_, _) => PreviewName.Text = NameBox.Text;
    }

    /// <summary>The sixteen colours, plus a way back to the default.</summary>
    private void BuildSwatches()
    {
        foreach (var code in Codes)
        {
            Swatches.Children.Add(new Button
            {
                Classes = { "swatch" },
                Background = new SolidColorBrush(MinecraftMotd.ColourOf(code)),
                Tag = code,
                [ToolTip.TipProperty] = "§" + code,
            });
            ((Button)Swatches.Children[^1]).Click += Swatch_Click;
        }

        // Not one of the sixteen: it removes the colour rather than setting one. Drawn as an empty
        // outline for the same reason — it is the absence of a colour, so it has none to show.
        var reset = new Button
        {
            Classes = { "swatch" },
            Background = Brushes.Transparent,
            Content = new ic_Cross(),
            Tag = null,
            [ToolTip.TipProperty] = Localizer.Get("Motd_Default"),
        };
        reset.Click += Swatch_Click;
        Swatches.Children.Add(reset);
    }

    /// <summary>A small × drawn without pulling in an icon font for one glyph.</summary>
    private sealed class ic_Cross : TextBlock
    {
        public ic_Cross()
        {
            Text = "×";
            FontSize = 14;
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            Opacity = 0.7;
        }
    }

    /// <summary>What the person has selected, as offsets into the plain text.</summary>
    private (int Start, int Length) Selection()
    {
        var a = Math.Min(PlainBox.SelectionStart, PlainBox.SelectionEnd);
        var b = Math.Max(PlainBox.SelectionStart, PlainBox.SelectionEnd);
        var text = PlainBox.Text ?? string.Empty;

        a = Math.Clamp(a, 0, text.Length);
        b = Math.Clamp(b, 0, text.Length);
        return (a, b - a);
    }

    private void Swatch_Click(object? sender, RoutedEventArgs e)
    {
        var (start, length) = Selection();
        if (length == 0) return;

        _runs = MotdDocument.Colour(_runs, start, length, (char?)((Button)sender!).Tag);
        RefreshAll(keepSelection: true);
    }

    private void Mark_Click(object? sender, RoutedEventArgs e)
    {
        var (start, length) = Selection();
        var toggle = (ToggleButton)sender!;

        if (length == 0) { toggle.IsChecked = false; return; }

        var style = Enum.Parse<MotdStyle>((string)toggle.Tag!);
        _runs = MotdDocument.Style(_runs, start, length, style, toggle.IsChecked == true);
        RefreshAll(keepSelection: true);
    }

    private void ClearFormat_Click(object? sender, RoutedEventArgs e)
    {
        var (start, length) = Selection();
        if (length == 0) return;

        _runs = MotdDocument.Clear(_runs, start, length);
        RefreshAll(keepSelection: true);
    }

    /// <summary>
    /// Typing in the clean box. The change is worked out by comparing with the document.
    /// </summary>
    /// <remarks>
    /// <c>TextChanged</c> gives the new text and nothing about what changed, so the edit is found by
    /// matching the common start and the common end. That is enough for every real edit — typing,
    /// deleting, pasting, replacing a selection — because all of them touch one contiguous stretch.
    /// </remarks>
    private void OnPlainChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        var before = MotdDocument.PlainText(_runs);
        var after = PlainBox.Text ?? string.Empty;
        if (before == after) return;

        var (start, removed, inserted) = MotdDocument.Diff(before, after);
        _runs = MotdDocument.Replace(_runs, start, removed, inserted);
        RefreshAll(exceptPlain: true, keepSelection: true);
    }

    /// <summary>Editing or pasting into the raw box. Whatever is there simply is the sign.</summary>
    private void OnCodeChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        _runs = MotdDocument.Parse(CodeBox.Text);
        RefreshAll(exceptCode: true);
    }

    private void RefreshAll(bool exceptPlain = false, bool exceptCode = false, bool keepSelection = false)
    {
        _syncing = true;
        try
        {
            var caret = PlainBox.CaretIndex;
            var (selStart, selLength) = Selection();

            PreviewText.Inlines?.Clear();
            PreviewText.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
            foreach (var inline in MinecraftMotd.Render(_runs)) PreviewText.Inlines.Add(inline);

            if (!exceptPlain) PlainBox.Text = MotdDocument.PlainText(_runs);
            if (!exceptCode) CodeBox.Text = MotdDocument.ToCode(_runs);

            if (keepSelection)
            {
                PlainBox.CaretIndex = caret;
                PlainBox.SelectionStart = selStart;
                PlainBox.SelectionEnd = selStart + selLength;
            }

            UpdateLineCount();
            UpdateMarks();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Says how many lines the sign is, and warns past two.
    /// </summary>
    /// <remarks>
    /// The client shows two and silently drops the rest, which is the sort of thing you discover
    /// from a friend rather than from your own screen.
    /// </remarks>
    private void UpdateLineCount()
    {
        var lines = MotdDocument.PlainText(_runs).Split('\n').Length;
        var over = lines > 2;

        LinesText.Text = over
            ? Localizer.Get("Motd_TooLong")
            : string.Format(Localizer.Get("Motd_LinesFmt"), lines);

        LinesText.Foreground = over
            ? new SolidColorBrush(Color.Parse("#E3A82B"))
            : Foreground;
        LinesText.Opacity = over ? 1 : 0.6;
    }

    /// <summary>Lights the marks that the selected text already has.</summary>
    /// <remarks>
    /// Read from the document rather than remembered, so the buttons describe what is selected
    /// instead of what was last pressed. A mixed selection shows unlit: pressing then applies the
    /// mark to all of it, which is what every editor does.
    /// </remarks>
    private void UpdateMarks()
    {
        var (start, length) = Selection();
        var selected = length > 0 ? Slice(start, length) : Array.Empty<MotdRun>();

        BoldToggle.IsChecked = selected.Count > 0 && selected.All(r => r.Bold);
        ItalicToggle.IsChecked = selected.Count > 0 && selected.All(r => r.Italic);
        UnderlineToggle.IsChecked = selected.Count > 0 && selected.All(r => r.Underline);
        StrikeToggle.IsChecked = selected.Count > 0 && selected.All(r => r.Strike);
    }

    /// <summary>The runs a selection covers, even partially.</summary>
    private IReadOnlyList<MotdRun> Slice(int start, int length)
    {
        var end = start + length;
        var result = new List<MotdRun>();
        var at = 0;

        foreach (var run in _runs)
        {
            var runEnd = at + run.Text.Length;
            if (runEnd > start && at < end) result.Add(run);
            at = runEnd;
        }

        return result;
    }

    /// <summary>
    /// Changes the icon players see next to the name.
    /// </summary>
    /// <remarks>
    /// Here and not on the server header, where it used to be a click on the picture itself. The
    /// icon and the sign are the same job — what the server looks like in somebody's server list —
    /// and they were in two different places, one of them hidden behind hovering over an image.
    /// Writing it immediately rather than on Save is deliberate: it is a file on disk, not a field,
    /// and the preview right here shows the result.
    /// </remarks>
    private async void ChangeIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (_folder is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectImage"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Localizer.Get("Title_SelectImage"))
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif" }
                }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            new ServerIconService().SetIconFromImage(_folder, path);
            IconChanged = true;

            var written = System.IO.Path.Combine(_folder, "server-icon.png");
            using var stream = System.IO.File.OpenRead(written);
            ServerIcon = new Bitmap(stream);
            OnPropertyChanged(nameof(ServerIcon));
            OnPropertyChanged(nameof(HasIcon));
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(
                string.Format(Localizer.Get("Msg_IconCreateError"), ex.Message),
                Localizer.Get("Title_ChangeIcon"), this);
        }
    }

    /// <summary>Whether the icon on disk was replaced, so the caller can reload it.</summary>
    public bool IconChanged { get; private set; }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        // Escaped on the way out: server.properties is line-oriented and cannot hold a real newline.
        Result = MotdDocument.Escape(MotdDocument.ToCode(_runs));
        // Un nombre en blanco no es un renombrado, es un descuido: se deja el que habia.
        ResultName = string.IsNullOrWhiteSpace(NameBox.Text) ? ServerName : NameBox.Text.Trim();
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Only the icon changes after the dialog is built, so this is all the plumbing needed.</summary>
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
