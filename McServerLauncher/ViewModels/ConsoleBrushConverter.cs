using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// A console line's kind to the brush it is drawn in.
/// </summary>
/// <remarks>
/// <para>
/// A converter rather than a brush stored on every line, for the reason that only shows up later:
/// the console holds two thousand lines, and a colour the user changes in the settings has to reach
/// all of them. Storing a brush per line means walking two thousand objects to repaint; asking a
/// converter means the change arrives for free the next time each visible row is drawn.
/// </para>
/// <para>
/// Reads the app-wide settings rather than a server's own. The per-server notification override
/// decides <em>which notifications appear</em> for that server, which is a different question from
/// what colour a log line is; a console that changed palette depending on which server was selected
/// would be a curiosity, not a feature.
/// </para>
/// </remarks>
public class ConsoleBrushConverter : IValueConverter
{
    public static readonly ConsoleBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value as ConsoleLineKind? ?? ConsoleLineKind.Info;

        return ConsolePalette.BrushFor(kind, NotificationPreferences.Global,
            ConsolePreferences.ChatColor, ConsolePreferences.PlayersColor);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
