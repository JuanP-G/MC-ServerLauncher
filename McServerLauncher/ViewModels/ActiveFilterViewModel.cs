using Avalonia.Media;

namespace McServerLauncher.ViewModels;

/// <summary>
/// One chip in the "what is filtering right now" row. Some of them are the server's own loader and
/// version, which are not the user's to remove; the rest are categories, which are.
/// </summary>
public class ActiveFilterViewModel
{
    public string Label { get; }

    public IBrush Brush { get; }

    public string Tip { get; }

    /// <summary>The tag behind the chip, or null for the fixed loader/version chips.</summary>
    public StoreTagViewModel? Tag { get; }

    public bool CanRemove => Tag is not null;

    public ActiveFilterViewModel(string label, IBrush brush, string tip)
    {
        Label = label;
        Brush = brush;
        Tip = tip;
    }

    public ActiveFilterViewModel(StoreTagViewModel tag)
    {
        Tag = tag;
        Label = tag.Label;
        Brush = tag.Brush;
        Tip = tag.Label;
    }
}
