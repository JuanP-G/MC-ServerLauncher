using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What the Discord presence may know about one running server. Nothing else leaves.</summary>
/// <param name="Name">The name the owner gave it.</param>
/// <param name="Type">Its type, for the brand name and the small logo.</param>
/// <param name="GameVersion">Minecraft version; empty when unknown.</param>
/// <param name="Players">How many are connected; null when the console cannot be trusted to say.</param>
/// <param name="MaxPlayers">The server's slots.</param>
/// <param name="StartedAtUtc">When this run began.</param>
public sealed record DiscordServer(
    string Name, ServerType Type, string GameVersion, int? Players, int MaxPlayers, DateTime StartedAtUtc);

/// <summary>The card Discord draws under "Playing MC Server Launcher".</summary>
public sealed record DiscordActivity(
    string Details, string? State, long StartUnixSeconds, string? SmallImage, string? SmallText)
{
    /// <summary>Discord rejects a longer line, and a shorter one than two characters.</summary>
    internal const int MaxLine = 128;

    // Public https images instead of art assets uploaded to the Discord application: the logos are
    // already in the repository, so a new server type needs no step in a web portal.
    private const string RawRoot = "https://raw.githubusercontent.com/JuanP-G/MC-ServerLauncher/main/";
    internal const string LargeImage = RawRoot + "web/assets/logo.png";

    /// <summary>
    /// One server shows its name, type and version; several show only how many, so the card never
    /// has to pick one of them. Null when nothing is running, which clears the presence.
    /// </summary>
    /// <remarks>
    /// The tunnel address and the player names are not parameters on purpose: anyone who can see
    /// the profile can read this card.
    /// </remarks>
    public static DiscordActivity? For(IReadOnlyList<DiscordServer> servers)
    {
        if (servers.Count == 0) return null;
        var start = new DateTimeOffset(DateTime.SpecifyKind(servers.Min(s => s.StartedAtUtc), DateTimeKind.Utc))
            .ToUnixTimeSeconds();

        if (servers.Count == 1)
        {
            var s = servers[0];
            var type = ServerTypeCatalog.For(s.Type).DisplayName;
            var kind = string.IsNullOrWhiteSpace(s.GameVersion) ? type : $"{type} {s.GameVersion.Trim()}";
            var state = s.Players is { } n
                ? Localizer.Format("Discord_OneStateFmt", kind, n, s.MaxPlayers)
                : kind;
            var name = s.Name.Trim();
            return new DiscordActivity(Fit(name.Length >= 2 ? name : type), Fit(state), start,
                RawRoot + $"McServerLauncher/Resources/TypeLogos/{s.Type.ToString().ToLowerInvariant()}.png",
                type);
        }

        // A total that leaves a server out would read as fewer players than there are.
        string? players = null;
        if (servers.All(s => s.Players is not null))
        {
            var total = servers.Sum(s => s.Players!.Value);
            players = total == 1
                ? Localizer.Get("Discord_OnePlayer")
                : Localizer.Format("Discord_PlayersFmt", total);
        }

        return new DiscordActivity(Fit(Localizer.Format("Discord_ServersFmt", servers.Count)), players,
            start, null, null);
    }

    private static string Fit(string line)
    {
        if (line.Length <= MaxLine) return line;
        // Not between the two halves of a surrogate pair: that is not valid text any more.
        var end = char.IsHighSurrogate(line[MaxLine - 1]) ? MaxLine - 1 : MaxLine;
        return line[..end];
    }
}
