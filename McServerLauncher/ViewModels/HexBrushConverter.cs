using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// A hex string to a brush, for the live preview beside each colour box in the settings.
/// </summary>
/// <remarks>
/// A converter rather than four properties on the dialog, following
/// <see cref="NoticeBrushConverter"/>: the preview has to update on every keystroke, and a binding
/// straight to the same string the box edits is what gives that for free. Anything unparseable —
/// which is most of what a half-typed colour looks like — shows as transparent rather than throwing
/// or freezing on the last good value, so the box visibly goes blank until the colour is complete.
/// </remarks>
public class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && NotificationPalette.IsValid(hex) && Color.TryParse(hex.Trim(), out var colour))
            return new SolidColorBrush(colour);

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
