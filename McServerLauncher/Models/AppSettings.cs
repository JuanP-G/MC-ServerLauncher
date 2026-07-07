namespace McServerLauncher.Models;

/// <summary>Global application settings (not per-server).</summary>
public class AppSettings
{
    /// <summary>
    /// Legacy Playit key with write permission, used to create/delete tunnels. Superseded by the
    /// partner setup-code flow (<see cref="PlayitAgentSecretKey"/>); kept for users still on the
    /// old model.
    /// </summary>
    public string? PlayitApiKey { get; set; }

    /// <summary>
    /// Per-user self-managed agent secret key obtained from the partner setup-code flow
    /// (/v1/partner/create_agent). Used as the <c>agent-key</c> for all tunnel management. Stored
    /// encrypted at rest (like <see cref="PlayitApiKey"/>).
    /// </summary>
    public string? PlayitAgentSecretKey { get; set; }

    /// <summary>The agent id that pairs with <see cref="PlayitAgentSecretKey"/> (tunnel origin).</summary>
    public string? PlayitAgentId { get; set; }

    /// <summary>UI language (es, en, pt, fr, de). Empty = system language.</summary>
    public string? Language { get; set; }

    /// <summary>Last app version the user has already seen (to show the what's-new screen after updating).</summary>
    public string? LastVersionSeen { get; set; }

    /// <summary>
    /// Global desktop-notification preferences: the master switch and which kinds are enabled.
    /// These apply to every server unless the server has its own override (see
    /// <see cref="ServerConfig.UseCustomNotifications"/>).
    /// </summary>
    public NotificationSettings Notifications { get; set; } = new();
}
