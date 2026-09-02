using static McServerLauncher.Services.PlayitApiService;

namespace McServerLauncher.Services;

/// <summary>What is wrong with a tunnel, if anything.</summary>
public enum TunnelHealth
{
    /// <summary>A server in this app is listening on its local port. Nothing to do.</summary>
    InUse,

    /// <summary>Nothing in this app listens where it points. Usually a deleted server left it behind.</summary>
    Orphan,

    /// <summary>Another tunnel points at the same local port and protocol. Only one of them can work.</summary>
    PortClash,
}

/// <summary>
/// Crosses the tunnels on the playit account against the servers this app knows about.
/// </summary>
/// <remarks>
/// <para>
/// Both halves already existed and never met. <c>GetRunDataAsync</c> has always returned
/// every tunnel on the account, and <see cref="CrossplayService.PortsHeldBy"/> has worked out which
/// server owns which port since 1.11.2 — but nothing put the two together, so a tunnel left behind
/// by a deleted server appeared nowhere at all, and a collision was only ever noticed as "it does
/// not connect" and diagnosed by reading the console.
/// </para>
/// <para>
/// Pure on purpose, and taking ports rather than <see cref="Models.ServerConfig"/>: a server's Java
/// port lives in <c>server.properties</c>, not in the config, so a function that took configs would
/// have to read files and could not be tested without them.
/// </para>
/// </remarks>
public static class TunnelInventory
{
    /// <summary>A server, reduced to the two ports a tunnel can point at.</summary>
    /// <param name="Name">What to show in the table when this server owns a tunnel.</param>
    /// <param name="JavaPort">From <c>server.properties</c>. Null when it cannot be read.</param>
    /// <param name="BedrockPort">From the config. Zero means crossplay is not set up.</param>
    public record ServerPorts(string Name, int? JavaPort, int BedrockPort);

    /// <summary>One tunnel, with who owns it and what is wrong with it.</summary>
    /// <param name="Tunnel">As the account reported it.</param>
    /// <param name="Owner">The server listening on its local port, or null when nothing does.</param>
    /// <param name="IsBedrock">UDP, which for this app always means the Bedrock half.</param>
    /// <param name="Health">Whether anything needs doing about it.</param>
    public record Row(PlayitTunnel Tunnel, string? Owner, bool IsBedrock, TunnelHealth Health)
    {
        /// <summary>Whether this row is one the user has to do something about.</summary>
        public bool NeedsAttention => Health != TunnelHealth.InUse;
    }

    /// <summary>
    /// Every tunnel, in the order the account returned them, saying who owns it and what is wrong.
    /// </summary>
    public static IReadOnlyList<Row> Build(
        IEnumerable<PlayitTunnel> tunnels, IEnumerable<ServerPorts> servers)
    {
        var all = tunnels.ToList();
        var known = servers.ToList();

        // Two tunnels that merely share a port number are not in conflict when one is TCP and the
        // other UDP: that is the ordinary shape of a crossplay server, and flagging it would make
        // the panel cry wolf on a perfectly healthy pair. The protocol is part of the identity —
        // the same mistake Match was written to stop the delete path making.
        var clashing = all
            .GroupBy(t => (t.LocalPort, t.IsUdp))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        return all.Select(t =>
        {
            var owner = OwnerOf(t, known);
            var health = clashing.Contains((t.LocalPort, t.IsUdp)) ? TunnelHealth.PortClash
                       : owner is null ? TunnelHealth.Orphan
                       : TunnelHealth.InUse;

            return new Row(t, owner, t.IsUdp, health);
        }).ToList();
    }

    /// <summary>The server listening where this tunnel points, or null.</summary>
    /// <remarks>
    /// A UDP tunnel can only belong to a Bedrock port and a TCP one only to a Java port. Matching on
    /// the number alone would let a Java server on 19133 adopt somebody else's Bedrock tunnel and
    /// report a genuine orphan as healthy.
    /// </remarks>
    private static string? OwnerOf(PlayitTunnel tunnel, IEnumerable<ServerPorts> servers) =>
        servers.FirstOrDefault(s => tunnel.IsUdp
                ? s.BedrockPort > 0 && s.BedrockPort == tunnel.LocalPort
                : s.JavaPort is { } java && java == tunnel.LocalPort)
            ?.Name;

    /// <summary>How many rows the user has to do something about.</summary>
    public static int AttentionCount(IEnumerable<Row> rows) => rows.Count(r => r.NeedsAttention);
}
