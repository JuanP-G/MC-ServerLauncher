using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.Text;

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

    /// <summary>The parsed form of each template seen, by the template's own text.</summary>
    /// <remarks>
    /// By text and not by key: the language can change while the app runs, and the same key then
    /// names a different template. There are a few hundred distinct templates at most, five
    /// languages each, so the table stays small.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, CompositeFormat> Templates = new();

    /// <summary>
    /// The translated text for <paramref name="key"/> with <paramref name="args"/> filled in, the
    /// same result as <c>string.Format(Get(key), args)</c> but with the template parsed once.
    /// </summary>
    /// <remarks>
    /// Formats with the current culture, as <see cref="string.Format(string, object[])"/> does, so
    /// numbers and dates come out as before. A template whose placeholders do not match
    /// <paramref name="args"/> throws <see cref="FormatException"/>, also as before.
    /// </remarks>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        var parsed = Templates.GetOrAdd(template, static text => CompositeFormat.Parse(text));
        return string.Format(CultureInfo.CurrentCulture, parsed, args);
    }
}
