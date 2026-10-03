using McServerLauncher.ViewModels;
using static McServerLauncher.Services.PlayitApiService;

namespace McServerLauncher.Services;

/// <summary>
/// What a server card should say about its addresses, given what the Playit account answered.
/// </summary>
/// <remarks>
/// <para>
/// The card used to keep whatever it had last shown whenever a lookup came back empty. That is right
/// when the account could not be asked — no connection is not news about the tunnel — and wrong when
/// the account answered and the tunnel is simply not there. The second case is what happens after a
/// server's port moves: its old address belonged to the old port, often to another server's tunnel,
/// and kept on screen it made two servers look as if they shared one address.
/// </para>
/// <para>
/// So the two are told apart here: <c>tunnels == null</c> means "could not ask" and keeps what there
/// was; a list, even an empty one, is the account's answer and is believed.
/// </para>
/// </remarks>
public static class TunnelAddressSync
{
    /// <summary>
    /// The Java address and state to show for a server on <paramref name="port"/>: the TCP tunnel's
    /// address, or nothing with the reason. Null when the account could not be asked, meaning
    /// "leave the address as it is, but say it failed".
    /// </summary>
    public static (string Address, TunnelAddressState State)? Java(IReadOnlyList<PlayitTunnel>? tunnels, int port)
    {
        if (tunnels is null) return null;

        var tunnel = Match(tunnels, port, udp: false);
        if (tunnel is null) return ("", TunnelAddressState.NoTunnel);
        return tunnel.Address is { Length: > 0 } address
            ? (address, TunnelAddressState.Ready)
            : ("", TunnelAddressState.Waiting);
    }

    /// <summary>
    /// The Bedrock host, public port and state to show for a crossplay server on <paramref name="port"/>.
    /// Null when the account could not be asked, meaning "leave the card as it is, but say it failed".
    /// </summary>
    public static (string Host, string PublicPort, BedrockAddressState State)? Bedrock(
        IReadOnlyList<PlayitTunnel>? tunnels, int port)
    {
        if (tunnels is null) return null;

        var tunnel = Match(tunnels, port, udp: true);
        if (tunnel is null) return ("", "", BedrockAddressState.LocalOnly);

        // A tunnel that exists but has no address or port yet is a few seconds from having one.
        if (tunnel.Address is not { Length: > 0 } host || tunnel.PublicPort <= 0)
            return ("", "", BedrockAddressState.Waiting);

        return (host, tunnel.PublicPort.ToString(), BedrockAddressState.Ready);
    }
}
