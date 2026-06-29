using System.Globalization;
using System.Resources;

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
