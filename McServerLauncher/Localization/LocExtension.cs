using Avalonia.Markup.Xaml;

namespace McServerLauncher.Localization;

/// <summary>
/// Markup extension to use translated texts in XAML: <c>{loc:Loc Key}</c>.
/// Since the language is applied at startup, it returns the text for the active language when the view loads.
/// </summary>
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Get(Key);
}
