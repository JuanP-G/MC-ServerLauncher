using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Background for the install banner: red when the install failed, green when it worked. Colour is
/// the part of the message people read first, so it carries the outcome as well as the words do.
/// </summary>
public class NoticeBrushConverter : IValueConverter
{
    public static readonly NoticeBrushConverter Instance = new();

    private static readonly IBrush Failure = new SolidColorBrush(Color.Parse("#B3341C"));
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#2C6B3F"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Failure : Success;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
