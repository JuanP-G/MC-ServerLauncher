using McServerLauncher.Services;

namespace McServerLauncher.Models;

/// <summary>The kinds of desktop notification the app can raise.</summary>
public enum NotificationKind
{
    PlayerJoined,
    PlayerLeft,
    PlayerDeath,
    ServerCrashed,
    AutoRestartGaveUp,

    /// <summary>An empty server stopped itself after the configured wait.</summary>
    IdleShutdown,

    /// <summary>A stopped server started itself because somebody tried to join.</summary>
    WokeOnDemand
}

/// <summary>How serious a notification is, which is what decides its colour.</summary>
/// <remarks>
/// Four levels rather than one colour per kind. Seven colours would be seven decisions for the user
/// and seven ways to end up with a palette that means nothing; four are enough to answer the only
/// question a glance asks — is this fine, or does it need me? — and adding an eighth kind then costs
/// a row in a table instead of a new colour nobody chose.
/// </remarks>
public enum NotificationLevel
{
    /// <summary>Something happened. No judgement attached.</summary>
    Info,

    /// <summary>Something went right.</summary>
    Success,

    /// <summary>Worth knowing about, nothing is broken.</summary>
    Warning,

    /// <summary>Something is broken or gave up.</summary>
    Error
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
    public bool IdleShutdown { get; set; } = true;
    public bool WokeOnDemand { get; set; } = true;

    // --- Colours, one per level ---
    // Stored as hex strings rather than a colour type on purpose: this class is serialized straight
    // to settings.json, and a string is what survives that unchanged and stays readable to somebody
    // editing the file by hand. Anything unparseable falls back to the default in NotificationPalette
    // rather than throwing — a mistyped colour must not be able to stop a crash notification.
    // The defaults are the greens, ambers and reds the rest of the app already uses.

    /// <summary>Colour for notifications that are neither good nor bad.</summary>
    public string ColorInfo { get; set; } = NotificationPalette.DefaultInfo;

    /// <summary>Colour for notifications that report something going right.</summary>
    public string ColorSuccess { get; set; } = NotificationPalette.DefaultSuccess;

    /// <summary>Colour for notifications worth knowing about.</summary>
    public string ColorWarning { get; set; } = NotificationPalette.DefaultWarning;

    /// <summary>Colour for notifications that report something broken.</summary>
    public string ColorError { get; set; } = NotificationPalette.DefaultError;

    /// <summary>The configured colour for a level, as a hex string.</summary>
    public string ColorFor(NotificationLevel level) => level switch
    {
        NotificationLevel.Success => ColorSuccess,
        NotificationLevel.Warning => ColorWarning,
        NotificationLevel.Error => ColorError,
        _ => ColorInfo
    };

    /// <summary>Sets the colour for a level. Used by the settings dialog.</summary>
    public void SetColorFor(NotificationLevel level, string hex)
    {
        switch (level)
        {
            case NotificationLevel.Success: ColorSuccess = hex; break;
            case NotificationLevel.Warning: ColorWarning = hex; break;
            case NotificationLevel.Error: ColorError = hex; break;
            default: ColorInfo = hex; break;
        }
    }

    /// <summary>True if this scope allows <paramref name="kind"/> (master on AND that kind on).</summary>
    public bool Allows(NotificationKind kind) => Enabled && kind switch
    {
        NotificationKind.PlayerJoined => PlayerJoined,
        NotificationKind.PlayerLeft => PlayerLeft,
        NotificationKind.PlayerDeath => PlayerDeath,
        NotificationKind.ServerCrashed => ServerCrashed,
        NotificationKind.AutoRestartGaveUp => AutoRestartGaveUp,
        NotificationKind.IdleShutdown => IdleShutdown,
        NotificationKind.WokeOnDemand => WokeOnDemand,
        _ => true
    };

    /// <summary>
    /// A copy (used to seed a per-server override from the global defaults). Copied field-by-field on
    /// purpose: if a reference-typed field is ever added here, a <c>MemberwiseClone</c> would silently
    /// share it between the global and per-server copies — this makes each field an explicit choice.
    /// </summary>
    public NotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        PlayerJoined = PlayerJoined,
        PlayerLeft = PlayerLeft,
        PlayerDeath = PlayerDeath,
        ServerCrashed = ServerCrashed,
        AutoRestartGaveUp = AutoRestartGaveUp,
        IdleShutdown = IdleShutdown,
        WokeOnDemand = WokeOnDemand,
        // Strings, so sharing them would do no harm — but leaving them out would: the settings
        // dialog edits a clone and copies it back on OK, so a field missing here is a field the
        // user can change and watch revert. That is the failure this comment was written for.
        ColorInfo = ColorInfo,
        ColorSuccess = ColorSuccess,
        ColorWarning = ColorWarning,
        ColorError = ColorError,
    };
}
