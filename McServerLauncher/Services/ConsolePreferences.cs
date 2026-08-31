namespace McServerLauncher.Services;

/// <summary>
/// The two console colours the user can change, app-wide.
/// </summary>
/// <remarks>
/// App-wide state set once at startup from <c>AppSettings</c> and updated when the settings dialog
/// is accepted — the same shape as <see cref="NotificationPreferences.Global"/>, and for the same
/// reason: every server draws its console the same way, so threading one more service through every
/// view model to say so would be ceremony with no decision in it.
///
/// Only these two live here. Errors, warnings and the app's own messages take their colour from the
/// notification settings, which are already per-server capable; these are not.
/// </remarks>
public static class ConsolePreferences
{
    /// <summary>Colour for player chat.</summary>
    public static string ChatColor { get; set; } = ConsoleColors.DefaultChat;

    /// <summary>Colour for joins, leaves and deaths.</summary>
    public static string PlayersColor { get; set; } = ConsoleColors.DefaultPlayers;
}
