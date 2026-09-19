namespace McServerLauncher.Models;

/// <summary>What the player history keeps, and for how long. App-wide.</summary>
/// <remarks>
/// <para>
/// On by default, with limits that keep it small: at most <see cref="MaxEventsPerPlayer"/> events per
/// player and nothing older than <see cref="RetentionDays"/>. That is about 50 KB per player at worst,
/// and far less for most.
/// </para>
/// <para>
/// There is deliberately no setting for IP addresses, because they are never kept. The server writes
/// one in every login line, and that line is not recorded.
/// </para>
/// </remarks>
public sealed class PlayerHistorySettings
{
    public const int MinEvents = 50, MaxEvents = 5000, DefaultEvents = 500;
    public const int MinDays = 7, MaxDays = 3650, DefaultDays = 90;

    /// <summary>Whether anything is recorded at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether what players say in chat is recorded. Joins, leaves and deaths are either way.</summary>
    public bool RecordChat { get; set; } = true;

    /// <summary>The newest events kept per player; older ones are dropped.</summary>
    public int MaxEventsPerPlayer { get; set; } = DefaultEvents;

    /// <summary>Events older than this are dropped, and so are players not seen for this long.</summary>
    public int RetentionDays { get; set; } = DefaultDays;

    /// <summary>The same settings with every number inside its allowed range.</summary>
    /// <remarks>settings.json is a file people can edit; a zero or a negative must not mean "keep nothing".</remarks>
    public PlayerHistorySettings Clamped() => new()
    {
        Enabled = Enabled,
        RecordChat = RecordChat,
        MaxEventsPerPlayer = Math.Clamp(MaxEventsPerPlayer, MinEvents, MaxEvents),
        RetentionDays = Math.Clamp(RetentionDays, MinDays, MaxDays)
    };
}
