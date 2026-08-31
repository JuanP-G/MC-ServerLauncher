using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Decides whether a given notification should be shown for a given server, combining the global
/// settings with an optional per-server override. <see cref="Global"/> is app-wide state (like
/// <c>ToastService.Shared</c>): set once at startup from <see cref="AppSettings"/> and updated when
/// the user edits the global notification settings.
/// </summary>
public static class NotificationPreferences
{
    /// <summary>Global defaults (the master switch + per-kind flags applied to every server).</summary>
    public static NotificationSettings Global { get; set; } = new();

    /// <summary>
    /// True if <paramref name="kind"/> should be shown for <paramref name="config"/>. The global
    /// master switch always wins (turning it off silences everything); otherwise a server using a
    /// custom override is judged by its own settings, and every other server by the global ones.
    /// </summary>
    public static bool ShouldNotify(ServerConfig config, NotificationKind kind)
    {
        if (!Global.Enabled) return false;
        return EffectiveFor(config).Allows(kind);
    }

    /// <summary>
    /// The settings that actually apply to a server: its own override, or the global ones.
    /// </summary>
    /// <remarks>
    /// Pulled out of <see cref="ShouldNotify"/> because the colours need exactly the same answer.
    /// Two copies of "which settings apply here" would drift the first time one of them changed,
    /// and the symptom would be a server notifying by its own rules and colouring by somebody
    /// else's — visible, confusing, and very hard to attribute.
    /// </remarks>
    public static NotificationSettings EffectiveFor(ServerConfig? config) =>
        config is { UseCustomNotifications: true, Notifications: { } custom } ? custom : Global;
}
