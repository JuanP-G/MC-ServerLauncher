using System.Collections.Generic;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What each kind of notification looks like: its level, and the emoji that identifies it.
/// </summary>
/// <remarks>
/// <para>
/// Built like <see cref="ServerTypeCatalog"/>, and for the same reason: the table holds data and
/// nothing from any UI framework, so it can be read by a test, and adding a kind is a row rather
/// than an edit in three files. The colours themselves are not here — they are a user setting, and
/// live in <see cref="NotificationSettings"/>.
/// </para>
/// <para>
/// Emoji rather than an icon font, exactly as in <see cref="ServerTypeCatalog.FamilyEmoji"/>: the
/// point of the marks is that "somebody joined" and "the server crashed" can be told apart without
/// reading, and shape does that where colour alone leaves out anybody who cannot separate red from
/// green. There is also a practical reason — a toast is built in code, and the headless test harness
/// cannot create the icon typeface at all.
/// </para>
/// </remarks>
public static class NotificationCatalog
{
    /// <summary>One kind of notification: how serious it is, and how it is marked.</summary>
    public record Entry(NotificationKind Kind, NotificationLevel Level, string Emoji);

    /// <summary>
    /// Every kind, with its level and mark.
    /// </summary>
    /// <remarks>
    /// The levels answer one question: is the server serving players, or is it not? Green when it
    /// is (somebody arrived, it woke up), grey when nothing changed either way (somebody left or
    /// died), amber when it is down on purpose, red when it is down and did not mean to be. The
    /// escalation from amber to red is the one worth having: a server that stopped itself because
    /// nobody was on it and a server that crashed are both "not running", and they need completely
    /// different reactions.
    ///
    /// The marks are written as escapes rather than pasted in, the same convention the server-type
    /// emoji follow: an emoji in a source file survives a careless encoding change far less well
    /// than six characters of ASCII, and this file is read by tests that compare exact strings.
    /// </remarks>
    public static readonly IReadOnlyList<Entry> All = new[]
    {
        // A player arriving is the one unambiguously good thing on this list.
        new Entry(NotificationKind.PlayerJoined, NotificationLevel.Success, "\U0001F44B"),   // waving hand
        new Entry(NotificationKind.PlayerLeft, NotificationLevel.Info, "\U0001F6AA"),        // door
        new Entry(NotificationKind.PlayerDeath, NotificationLevel.Info, "\U0001F480"),       // skull
        new Entry(NotificationKind.ServerCrashed, NotificationLevel.Error, "\U0001F4A5"),    // collision
        new Entry(NotificationKind.AutoRestartGaveUp, NotificationLevel.Error, "\u26D4"),    // no entry
        // Down, but because it was told to be. Not an error, and not nothing either: if you were
        // away expecting it up, this is the line on the list you might want to do something about.
        new Entry(NotificationKind.IdleShutdown, NotificationLevel.Warning, "\U0001F634"),   // sleeping
        new Entry(NotificationKind.WokeOnDemand, NotificationLevel.Success, "\u23F0")        // alarm clock
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
            : new Entry(kind, NotificationLevel.Info, string.Empty);

    /// <summary>The level a kind is shown at.</summary>
    public static NotificationLevel LevelOf(NotificationKind kind) => For(kind).Level;

    /// <summary>The mark shown beside a kind.</summary>
    public static string EmojiOf(NotificationKind kind) => For(kind).Emoji;
}
