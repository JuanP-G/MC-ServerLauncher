using Avalonia.Media;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The configured notification colours, as something Avalonia can draw with.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="ServerTypeBrushes"/>, and split from
/// <see cref="NotificationPalette"/> for the same reason that one is split from
/// <c>ServerTypeCatalog</c>: the strings are a setting and belong somewhere that knows nothing about
/// a UI framework, and the conversion belongs somewhere a UI can reach. Not cached, unlike the
/// server-type brushes — these change whenever the user edits them, and a toast is built a handful
/// of times a day.
/// </remarks>
public static class NotificationBrushes
{
    /// <summary>The colour for a level, falling back to the default for anything unparseable.</summary>
    public static Color ColorFor(NotificationSettings? settings, NotificationLevel level)
    {
        var hex = NotificationPalette.Sanitize(settings?.ColorFor(level), level);

        // Sanitize has already accepted the shape, but the framework is the last word on whether it
        // can be drawn — and this runs while building a toast that might be reporting a crash.
        return Color.TryParse(hex, out var colour) ? colour : Colors.Gray;
    }

    /// <summary>The accent for a level: the border of the toast and its mark.</summary>
    public static IBrush BrushFor(NotificationSettings? settings, NotificationLevel level) =>
        new SolidColorBrush(ColorFor(settings, level));

    /// <summary>
    /// The same colour faded, for a surface the text has to stay readable against.
    /// </summary>
    /// <remarks>
    /// Derived from the accent rather than configured on its own. Two colours per level would be
    /// eight settings and eight chances to choose a pair nobody can read; one alpha over the colour
    /// the user picked cannot produce that.
    /// </remarks>
    public static IBrush FadedBrushFor(NotificationSettings? settings, NotificationLevel level,
        byte alpha = 0x66)
    {
        var c = ColorFor(settings, level);
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}
