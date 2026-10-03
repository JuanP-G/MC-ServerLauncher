using static McServerLauncher.Services.PlayitApiService;

namespace McServerLauncher.Services;

/// <summary>
/// What a server needs from the account's tunnels: its name and the ports it listens on.
/// <c>JavaPort</c> is null when <c>server.properties</c> cannot be read; <c>BedrockPort</c> is null when
/// crossplay is off (a server without Bedrock owns no UDP tunnel); <c>PlayitEnabled</c> says whether
/// the server is meant to be reached through Playit at all.
/// </summary>
public sealed record ServerPorts(string Id, string Name, int? JavaPort, int? BedrockPort, bool PlayitEnabled);

/// <summary>How a tunnel stands against the servers on this machine.</summary>
public enum TunnelHealth
{
    /// <summary>Exactly one server listens where the tunnel points.</summary>
    Ok,

    /// <summary>No server listens where the tunnel points: it carries traffic to nothing.</summary>
    Orphan,

    /// <summary>The account holds more than one tunnel for the same local port and protocol.</summary>
    Duplicate,

    /// <summary>Two or more servers share the port the tunnel points at, so only one can answer.</summary>
    Shared,
}

public enum TunnelSuggestionKind
{
    /// <summary>Delete a tunnel that leads nowhere.</summary>
    DeleteOrphan,

    /// <summary>Delete the extra copy of a tunnel that exists twice.</summary>
    DeleteDuplicate,

    /// <summary>Two servers on one port: change the port of one of them.</summary>
    SharedPort,

    /// <summary>A server meant for Playit has no Java tunnel.</summary>
    CreateJava,

    /// <summary>A crossplay server has no Bedrock tunnel.</summary>
    CreateBedrock,
}

/// <summary>
/// One thing the screen can offer to put right. <c>TunnelId</c> and <c>ServerId</c> name what it
/// concerns, when it concerns one; <c>Subject</c> is the name to show (the tunnel's or the server's);
/// <c>Other</c> is a second name where one is needed (the other server sharing a port); <c>Udp</c>
/// and <c>Port</c> say which local port it is about — a shared Bedrock port is fixed differently
/// from a shared Java one, and "create a tunnel" is only actionable on playit.gg with the port in hand.
/// </summary>
public sealed record TunnelSuggestion(
    TunnelSuggestionKind Kind, string? TunnelId, string? ServerId, string Subject, string? Other = null,
    bool Udp = false, int Port = 0)
{
    /// <summary>Every suggestion left is something wrong; the tidy-up of names went (it was noise).</summary>
    public bool IsProblem => true;
}

/// <summary>
/// One tunnel of the account, with what is known about it. <c>Owners</c> are the servers that listen
/// where it points; <c>SuggestedName</c> is the name it would carry if named after its server, null
/// when nothing sensible exists (no owner, or several).
/// </summary>
public sealed record TunnelRow(
    PlayitTunnel Tunnel, TunnelHealth Health, IReadOnlyList<ServerPorts> Owners, string? SuggestedName)
{
    public bool NameDiffers => SuggestedName is not null && !string.Equals(Tunnel.Name, SuggestedName, StringComparison.Ordinal);
}

public sealed record TunnelReport(IReadOnlyList<TunnelRow> Rows, IReadOnlyList<TunnelSuggestion> Suggestions)
{
    /// <summary>How many rows have something wrong with them (renaming is a tidy-up, not a problem).</summary>
    public int ProblemCount => Rows.Count(r => r.Health != TunnelHealth.Ok);
}

/// <summary>
/// Reading the account's tunnels against the servers on this machine: which ones lead nowhere, which
/// ones two servers are fighting over, which are there twice, and what is missing.
/// </summary>
/// <remarks>
/// <para>
/// Pure and without network access, so every rule can be pinned by a test. The identity of a tunnel
/// is its local port <em>and</em> its protocol, the same definition <see cref="PlayitApiService.Match"/>
/// uses: TCP belongs to a server's Java port, UDP to its Bedrock port. Matching on the port alone
/// would let a Bedrock tunnel look like a server's Java one whenever the two numbers coincided.
/// </para>
/// <para>
/// It only ever reads. Nothing here deletes or renames anything; it says what it would suggest and
/// the caller decides, because deleting a tunnel cannot be undone.
/// </para>
/// </remarks>
public static class TunnelInventory
{
    private const string BedrockSuffix = " (Bedrock)";

    /// <summary>The name a tunnel is created under, and recognised by: the server's, plus a suffix for Bedrock.</summary>
    public static string NameFor(string serverName, bool udp) => udp ? serverName + BedrockSuffix : serverName;

    public static TunnelReport Build(IReadOnlyList<PlayitTunnel> tunnels, IReadOnlyList<ServerPorts> servers)
    {
        var rows = new List<TunnelRow>();
        var suggestions = new List<TunnelSuggestion>();

        foreach (var tunnel in tunnels)
        {
            var owners = OwnersOf(tunnel, servers);
            var copies = tunnels.Where(t => SameTunnel(t, tunnel)).ToList();
            // The first copy is the one to keep, unless another is already named the way it would be
            // created; keeping the oddly-named one and deleting the tidy one would be perverse.
            var keeper = KeeperAmong(copies, owners);

            var health =
                owners.Count == 0 ? TunnelHealth.Orphan :
                copies.Count > 1 ? TunnelHealth.Duplicate :
                owners.Count > 1 ? TunnelHealth.Shared :
                TunnelHealth.Ok;

            var suggested = owners.Count == 1 ? NameFor(owners[0].Name, tunnel.IsUdp) : null;
            rows.Add(new TunnelRow(tunnel, health, owners, suggested));

            if (owners.Count == 0)
                suggestions.Add(new(TunnelSuggestionKind.DeleteOrphan, tunnel.Id, null, DisplayName(tunnel)));
            else if (copies.Count > 1 && !ReferenceEquals(tunnel, keeper))
                // Named by its address, not its name: the two copies usually carry the same name, and
                // "delete Survival2" would not say which one.
                suggestions.Add(new(TunnelSuggestionKind.DeleteDuplicate, tunnel.Id, owners[0].Id,
                    tunnel.Address ?? DisplayName(tunnel), owners[0].Name));
        }

        // One suggestion per shared tunnel, not per copy: a duplicated shared tunnel would otherwise
        // be reported twice for one underlying mistake.
        foreach (var group in rows.Where(r => r.Owners.Count > 1).GroupBy(r => (r.Tunnel.LocalPort, r.Tunnel.IsUdp)))
        {
            var row = group.First();
            // Move the server the tunnel is not named after; when neither matches, the later one.
            var mover = row.Owners.LastOrDefault(o => !string.Equals(
                              row.Tunnel.Name, NameFor(o.Name, row.Tunnel.IsUdp), StringComparison.Ordinal))
                        ?? row.Owners[^1];
            var other = row.Owners.First(o => o.Id != mover.Id);
            suggestions.Add(new(TunnelSuggestionKind.SharedPort, row.Tunnel.Id, mover.Id, mover.Name, other.Name,
                Udp: row.Tunnel.IsUdp, Port: row.Tunnel.LocalPort));
        }

        foreach (var server in servers.Where(s => s.PlayitEnabled))
        {
            if (server.JavaPort is { } java && !tunnels.Any(t => !t.IsUdp && t.LocalPort == java))
                suggestions.Add(new(TunnelSuggestionKind.CreateJava, null, server.Id, server.Name, Port: java));

            if (server.BedrockPort is { } bedrock && !tunnels.Any(t => t.IsUdp && t.LocalPort == bedrock))
                suggestions.Add(new(TunnelSuggestionKind.CreateBedrock, null, server.Id, server.Name, Udp: true, Port: bedrock));
        }

        // No suggestion for tunnels that work but carry another name. It was offered once and was
        // noise: a name is a label, not a fault, and the row's own "use this name" chip covers it.

        return new TunnelReport(rows, suggestions);
    }

    /// <summary>The renames that keep a server's tunnels in step after the server itself was renamed.</summary>
    /// <param name="server">The server, carrying its <em>new</em> name.</param>
    /// <param name="oldName">The name it had.</param>
    /// <param name="tunnels">Every tunnel of the account.</param>
    /// <param name="servers">Every server on this machine, to tell whose a tunnel is.</param>
    /// <remarks>
    /// Only a tunnel still carrying exactly the name the app would have given it is touched. One the
    /// user named by hand, on the website or here, is theirs and stays as it is; the app only follows
    /// its own naming, never overwrites somebody else's.
    /// </remarks>
    public static IReadOnlyList<(string TunnelId, string NewName)> RenamesFor(
        ServerPorts server, string oldName, IReadOnlyList<PlayitTunnel> tunnels, IReadOnlyList<ServerPorts> servers)
    {
        var renames = new List<(string, string)>();

        foreach (var tunnel in tunnels)
        {
            if (string.IsNullOrEmpty(tunnel.Id)) continue;

            var owners = OwnersOf(tunnel, servers);
            if (owners.Count != 1 || owners[0].Id != server.Id) continue;

            if (!string.Equals(tunnel.Name, NameFor(oldName, tunnel.IsUdp), StringComparison.Ordinal)) continue;

            var next = NameFor(server.Name, tunnel.IsUdp);
            if (!string.Equals(tunnel.Name, next, StringComparison.Ordinal))
                renames.Add((tunnel.Id, next));
        }

        return renames;
    }

    private static List<ServerPorts> OwnersOf(PlayitTunnel tunnel, IReadOnlyList<ServerPorts> servers) =>
        servers.Where(s => (tunnel.IsUdp ? s.BedrockPort : s.JavaPort) == tunnel.LocalPort).ToList();

    private static bool SameTunnel(PlayitTunnel a, PlayitTunnel b) =>
        a.LocalPort == b.LocalPort && a.IsUdp == b.IsUdp;

    private static PlayitTunnel KeeperAmong(List<PlayitTunnel> copies, List<ServerPorts> owners)
    {
        if (owners.Count == 1)
        {
            var wanted = NameFor(owners[0].Name, copies[0].IsUdp);
            var tidy = copies.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.Ordinal));
            if (tidy is not null) return tidy;
        }
        return copies[0];
    }

    private static string DisplayName(PlayitTunnel t) =>
        string.IsNullOrWhiteSpace(t.Name) ? (t.Address ?? t.Id) : t.Name;
}
