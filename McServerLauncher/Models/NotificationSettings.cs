namespace McServerLauncher.Models;

/// <summary>The kinds of desktop notification the app can raise.</summary>
public enum NotificationKind
{
    PlayerJoined,
    PlayerLeft,
    PlayerDeath,
    ServerCrashed,
    AutoRestartGaveUp
}

/// <summary>
/// Which notifications are enabled, for a given scope. Used both globally (default for every
/// server, in <see cref="AppSettings"/>) and per-server (an override, in <see cref="ServerConfig"/>).
/// <see cref="Enabled"/> is the master switch for the scope; the per-kind flags are only consulted
/// when it's on.
/// </summary>
public class NotificationSettings
{
    /// <summary>Master switch for this scope. When false, nothing is shown.</summary>
    public bool Enabled { get; set; } = true;

    public bool PlayerJoined { get; set; } = true;
    public bool PlayerLeft { get; set; } = true;
    public bool PlayerDeath { get; set; } = true;
    public bool ServerCrashed { get; set; } = true;
    public bool AutoRestartGaveUp { get; set; } = true;

    /// <summary>True if this scope allows <paramref name="kind"/> (master on AND that kind on).</summary>
    public bool Allows(NotificationKind kind) => Enabled && kind switch
    {
        NotificationKind.PlayerJoined => PlayerJoined,
        NotificationKind.PlayerLeft => PlayerLeft,
        NotificationKind.PlayerDeath => PlayerDeath,
        NotificationKind.ServerCrashed => ServerCrashed,
        NotificationKind.AutoRestartGaveUp => AutoRestartGaveUp,
        _ => true
    };

    /// <summary>A shallow copy (used to seed a per-server override from the global defaults).</summary>
    public NotificationSettings Clone() => (NotificationSettings)MemberwiseClone();
}
