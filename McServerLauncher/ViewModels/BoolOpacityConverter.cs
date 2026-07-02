using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace McServerLauncher.ViewModels;

/// <summary>true -> fully opaque, false -> dimmed. Used to fade disabled mods in the list.</summary>
public class BoolOpacityConverter : IValueConverter
{
    public static readonly BoolOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.45;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
