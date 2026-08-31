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

    // --- Console colours ---
    // Only the two the console has that notifications do not. Errors, warnings and the app's own
    // messages take their colour from Notifications above, so red means the same thing in a toast
    // and in the console; putting a chat colour inside NotificationSettings would have made that
    // class mean something it does not, and it is cloned per server, which chat colours are not.

    /// <summary>Colour for player chat in the console.</summary>
    public string ConsoleChatColor { get; set; } = Services.ConsoleColors.DefaultChat;

    /// <summary>Colour for joins, leaves and deaths in the console.</summary>
    public string ConsolePlayersColor { get; set; } = Services.ConsoleColors.DefaultPlayers;

    /// <summary>
    /// Minimizing sends the window to the tray (it leaves the taskbar) instead of minimizing normally.
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// Closing the window with the X sends it to the tray instead of quitting the app. Off by default:
    /// the X quits, which is what most people expect.
    /// </summary>
    public bool CloseToTray { get; set; }

    /// <summary>
    /// A field-for-field copy.
    /// </summary>
    /// <remarks>
    /// Saving needs an instance with the secrets swapped for their encrypted form while the
    /// caller's copy keeps the usable plaintext. Building that by listing the properties by hand is
    /// what silently stopped <see cref="MinimizeToTray"/> and <see cref="CloseToTray"/> from ever
    /// being written: they were added to this class and nobody added them there. Copying everything
    /// means a new setting is persisted without touching the save path at all.
    /// </remarks>
    public AppSettings ShallowCopy() => (AppSettings)MemberwiseClone();
}
