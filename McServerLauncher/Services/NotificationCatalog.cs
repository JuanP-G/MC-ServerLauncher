using System.Collections.Generic;
using System.Linq;
using FluentIcons.Common;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What each kind of notification looks like: its level, and the icon that identifies it.
/// </summary>
/// <remarks>
/// <para>
/// Built like <see cref="ServerTypeCatalog"/>, and for the same reason: the table holds data and
/// nothing from any UI framework's controls, so it can be read by a test, and adding a kind is a row
/// rather than an edit in three files. The colours themselves are not here — they are a user
/// setting, and live in <see cref="NotificationSettings"/>.
/// </para>
/// <para>
/// The icon is a <see cref="Symbol"/>, which is only a name: the same enum the rest of the app draws
/// its icons from, so a toast looks like the window it came from. The point of the marks is that
/// "somebody joined" and "the server crashed" can be told apart without reading, and shape does that
/// where colour alone leaves out anybody who cannot separate red from green — which is why no two
/// kinds share one.
/// </para>
/// </remarks>
public static class NotificationCatalog
{
    /// <summary>One kind of notification: how serious it is, and how it is marked.</summary>
    public record Entry(NotificationKind Kind, NotificationLevel Level, Symbol Icon);

    /// <summary>
    /// Every kind, with its level and mark.
    /// </summary>
    /// <remarks>
    /// The levels answer one question: is the server serving players, or is it not? Green when it
    /// is (somebody arrived, it woke up), grey-blue when nothing changed either way (somebody left or
    /// died), amber when it is down on purpose, red when it is down and did not mean to be. The
    /// escalation from amber to red is the one worth having: a server that stopped itself because
    /// nobody was on it and a server that crashed are both "not running", and they need completely
    /// different reactions.
    /// </remarks>
    public static readonly IReadOnlyList<Entry> All = new[]
    {
        // A player arriving is the one unambiguously good thing on this list.
        new Entry(NotificationKind.PlayerJoined, NotificationLevel.Success, Symbol.PersonAdd),
        new Entry(NotificationKind.PlayerLeft, NotificationLevel.Info, Symbol.SignOut),
        new Entry(NotificationKind.PlayerDeath, NotificationLevel.Info, Symbol.PersonDelete),
        new Entry(NotificationKind.ServerCrashed, NotificationLevel.Error, Symbol.ErrorCircle),
        new Entry(NotificationKind.AutoRestartGaveUp, NotificationLevel.Error, Symbol.PlugDisconnected),
        // Down, but because it was told to be. Not an error, and not nothing either: if you were
        // away expecting it up, this is the line on the list you might want to do something about.
        new Entry(NotificationKind.IdleShutdown, NotificationLevel.Warning, Symbol.WeatherMoon),
        new Entry(NotificationKind.WokeOnDemand, NotificationLevel.Success, Symbol.Power)
    };

    private static readonly Dictionary<NotificationKind, Entry> ByKind =
        All.ToDictionary(e => e.Kind);

    /// <summary>
    /// The entry for a kind. Falls back to a neutral one rather than throwing.
    /// </summary>
    /// <remarks>
    /// A notification that cannot be styled is still a notification worth showing: refusing to
    /// display "the server crashed" because nobody added it to this table would be the worst
    /// possible trade. <c>EveryKindIsInTheCatalogue</c> is what stops it silently coming to that.
    /// </remarks>
    public static Entry For(NotificationKind kind) =>
        ByKind.TryGetValue(kind, out var entry)
            ? entry
            : new Entry(kind, NotificationLevel.Info, Symbol.Alert);

    /// <summary>The level a kind is shown at.</summary>
    public static NotificationLevel LevelOf(NotificationKind kind) => For(kind).Level;

    /// <summary>The icon shown for a kind.</summary>
    public static Symbol IconOf(NotificationKind kind) => For(kind).Icon;

    /// <summary>
    /// The resource key of the line that names a kind at the top of its toast ("Player joined").
    /// </summary>
    /// <remarks>
    /// A key rather than the text, so this stays free of the localizer; the caller looks it up. It is
    /// built from the kind's name, which is why <c>LocalizationTests</c> lists the prefix among the
    /// keys that are made up at run time.
    /// </remarks>
    public static string HeadingKey(NotificationKind kind) => "Notif_Head_" + kind;
}
