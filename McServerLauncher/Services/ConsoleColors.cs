using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// The colour each kind of console line is drawn in, as data.
/// </summary>
/// <remarks>
/// <para>
/// Free of any UI framework, the same split <see cref="NotificationPalette"/> keeps from
/// <c>NotificationBrushes</c>: hex strings are data and can be referenced from
/// <see cref="AppSettings"/>, which is serialized to settings.json and should not know Avalonia
/// exists. The brushes are built next door in <c>ConsolePalette</c>.
/// </para>
/// <para>
/// Four of the six kinds take their colour from the notification settings, on purpose: red should
/// mean the same thing in a toast and in the console, and asking somebody to pick "the error colour"
/// twice is asking for an app that contradicts itself. Only chat and player events are the console's
/// own, because notifications have no equivalent of either.
/// </para>
/// <para>
/// Two kinds are deliberately not configurable at all. Ordinary output keeps the grey it has always
/// had — it is the vast majority of every log, and colouring all of it would be the same as
/// colouring none of it. A command you typed is white, because it is the one thing on screen that
/// came from you and not from the server.
/// </para>
/// </remarks>
public static class ConsoleColors
{
    /// <summary>Ordinary server output: the grey the console has always used.</summary>
    public const string Info = "#DDDDDD";

    /// <summary>A command you typed, echoed back.</summary>
    public const string Command = "#FFFFFF";

    /// <summary>Player chat. Distinct from everything the server says on its own.</summary>
    public const string DefaultChat = "#9CDCFE";

    /// <summary>Somebody joining, leaving or dying.</summary>
    public const string DefaultPlayers = "#C5A5F5";

    /// <summary>The hex a kind is drawn in, given the settings that apply.</summary>
    public static string HexFor(ConsoleLineKind kind, NotificationSettings? levels,
        string? chat, string? players) => kind switch
    {
        ConsoleLineKind.Error => NotificationPalette.Sanitize(
            levels?.ColorError, NotificationLevel.Error),

        ConsoleLineKind.Warn => NotificationPalette.Sanitize(
            levels?.ColorWarning, NotificationLevel.Warning),

        // The app talking. Takes the notification "info" blue rather than the server's grey,
        // because telling those two apart is the entire point of the category.
        ConsoleLineKind.Launcher => NotificationPalette.Sanitize(
            levels?.ColorInfo, NotificationLevel.Info),

        ConsoleLineKind.Chat => NotificationPalette.IsValid(chat) ? chat!.Trim() : DefaultChat,
        ConsoleLineKind.Players => NotificationPalette.IsValid(players) ? players!.Trim() : DefaultPlayers,
        ConsoleLineKind.Command => Command,
        _ => Info
    };
}
