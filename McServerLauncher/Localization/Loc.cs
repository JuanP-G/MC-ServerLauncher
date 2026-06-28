using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace McServerLauncher.Localization;

/// <summary>Acceso a los textos traducidos (Resources/Strings.resx y sus idiomas).</summary>
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
/// Extensión de marcado para usar textos traducidos en XAML: <c>{loc:Loc Clave}</c>.
/// Como el idioma se aplica al iniciar, devuelve el texto del idioma activo al cargar la vista.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Get(Key);
}
