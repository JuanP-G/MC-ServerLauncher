using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace McServerLauncher.Localization;

/// <summary>Access to the translated texts (Resources/Strings.resx and its languages).</summary>
public static class Localizer
{
    private static readonly ResourceManager Rm =
        new("McServerLauncher.Resources.Strings", typeof(Localizer).Assembly);

    public static string Get(string key)
    {
        try { return Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key; }
        catch { return key; }
    }
}

/// <summary>
/// Markup extension to use translated texts in XAML: <c>{loc:Loc Key}</c>.
/// Since the language is applied at startup, it returns the text for the active language when the view loads.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Get(Key);
}
