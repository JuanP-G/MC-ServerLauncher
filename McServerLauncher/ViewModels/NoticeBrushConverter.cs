using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Background for the install banner: red when the install failed, green when it worked. Colour is
/// the part of the message people read first, so it carries the outcome as well as the words do.
/// </summary>
/// <remarks>
/// The two colours are the <c>SemanticDangerBanner</c> and <c>SemanticOkBanner</c> resources, looked up
/// when they are needed so there is one place to change them. The literals below are only what is
/// used if the resources are not there, which is the case in a test with no application.
/// </remarks>
public class NoticeBrushConverter : IValueConverter
{
    public static readonly NoticeBrushConverter Instance = new();

    private static readonly IBrush FailureFallback = new SolidColorBrush(Color.Parse("#4B2E2F"));
    private static readonly IBrush SuccessFallback = new SolidColorBrush(Color.Parse("#2D4234"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? Find("SemanticDangerBanner") ?? FailureFallback
            : Find("SemanticOkBanner") ?? SuccessFallback;

    private static IBrush? Find(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var found)
            ? found as IBrush
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
