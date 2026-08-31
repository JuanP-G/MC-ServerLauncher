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
/// The four values are the ones the rest of the app has been using by hand all along, so a
/// notification looks like it belongs to the app rather than to a palette invented for it.
/// </para>
/// </remarks>
public static partial class NotificationPalette
{
    /// <summary>Neither good nor bad: the blue already used by the update banner.</summary>
    public const string DefaultInfo = "#2F6FB0";

    /// <summary>Something went right: the green already used for a healthy server.</summary>
    public const string DefaultSuccess = "#3FB950";

    /// <summary>Worth knowing: the amber already used by every warning panel.</summary>
    public const string DefaultWarning = "#E3A82B";

    /// <summary>Something is broken: the red already used by the tunnel warning.</summary>
    public const string DefaultError = "#E05561";

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
