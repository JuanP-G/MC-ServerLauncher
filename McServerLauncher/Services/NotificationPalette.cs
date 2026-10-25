using System.Text.RegularExpressions;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// The default colour of each notification level, and what counts as a valid one.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of any UI framework, the same split <see cref="ServerTypeCatalog"/> keeps from
/// <c>ServerTypeBrushes</c>: hex strings are data, a <c>Brush</c> is not. It means the defaults can
/// be referenced from <see cref="NotificationSettings"/> — which is serialized to settings.json and
/// should not know Avalonia exists — and that the validation can be tested without a UI at all.
/// </para>
/// <para>
/// The four values are the ones the rest of the app draws its status marks, panels and console
/// lines with (the <c>Semantic*</c> tokens in <c>App.axaml</c> say the same), so a notification looks
/// like it belongs to the app rather than to a palette invented for it. They are muted on purpose:
/// bright enough to read on the dark card at a glance, quiet enough not to shout.
/// </para>
/// </remarks>
public static partial class NotificationPalette
{
    /// <summary>Neither good nor bad: a calm blue.</summary>
    public const string DefaultInfo = "#7BA6CE";

    /// <summary>Something went right: a soft green, also the colour of a healthy server.</summary>
    public const string DefaultSuccess = "#6FB57F";

    /// <summary>Worth knowing: a dull amber, the one every warning panel uses.</summary>
    public const string DefaultWarning = "#D9B665";

    /// <summary>Something is broken: a dusty red, not an alarm.</summary>
    public const string DefaultError = "#DB7A79";

    // The four defaults before the notices were toned down. They are kept only to recognise them in
    // a settings file written by an older version; nothing draws with them any more.
    private const string LegacyInfo = "#2F6FB0";
    private const string LegacySuccess = "#3FB950";
    private const string LegacyWarning = "#E3A82B";
    private const string LegacyError = "#E05561";

    /// <summary>
    /// Moves a colour a settings file still holds from the old default of its level to the new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The colours are always written to disk, even untouched, so a file saved by an older version
    /// is full of the old defaults and changing the constants alone would only reach new installs.
    /// Nothing says whether a stored value was chosen or just never changed, so the rule is the
    /// simple one: exactly the old default becomes the new one, anything else is somebody's choice
    /// and is left alone. The cost is that a person who deliberately picked the old green gets the
    /// new one; the release notes say so.
    /// </para>
    /// <para>
    /// Safe to run on every load: once a colour has moved it no longer matches, so a second pass
    /// finds nothing to do.
    /// </para>
    /// </remarks>
    public static void ModerniseColours(NotificationSettings? settings)
    {
        if (settings is null) return;

        settings.ColorInfo = Moved(settings.ColorInfo, LegacyInfo, DefaultInfo);
        settings.ColorSuccess = Moved(settings.ColorSuccess, LegacySuccess, DefaultSuccess);
        settings.ColorWarning = Moved(settings.ColorWarning, LegacyWarning, DefaultWarning);
        settings.ColorError = Moved(settings.ColorError, LegacyError, DefaultError);
    }

    private static string Moved(string? stored, string legacy, string current) =>
        string.Equals(stored?.Trim(), legacy, StringComparison.OrdinalIgnoreCase)
            ? current
            : stored ?? current;

    /// <summary>The default hex for a level.</summary>
    public static string DefaultFor(NotificationLevel level) => level switch
    {
        NotificationLevel.Success => DefaultSuccess,
        NotificationLevel.Warning => DefaultWarning,
        NotificationLevel.Error => DefaultError,
        _ => DefaultInfo
    };

    /// <summary>
    /// Whether a string is a colour this app will draw with.
    /// </summary>
    /// <remarks>
    /// Its own check rather than the framework's parser, for two reasons. It runs in a test with no
    /// UI, and it is stricter on purpose: the settings box is a free-text field, and accepting
    /// everything a parser tolerates — colour names, shorthand nobody typed on purpose — would let
    /// a value through that renders as something the user did not intend and cannot explain.
    /// </remarks>
    public static bool IsValid(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && HexColour().IsMatch(hex.Trim());

    /// <summary>The colour, or the level's default when it is not one this app will draw with.</summary>
    /// <remarks>
    /// Never throws and never returns nothing. These colours are read while building a toast that
    /// may be saying the server crashed, and a value mistyped in a settings box must not be able to
    /// turn that into an exception nobody sees.
    /// </remarks>
    public static string Sanitize(string? hex, NotificationLevel level) =>
        IsValid(hex) ? hex!.Trim() : DefaultFor(level);

    // #RGB, #RRGGBB or #AARRGGBB — what Avalonia draws and what a person actually types.
    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColour();
}
