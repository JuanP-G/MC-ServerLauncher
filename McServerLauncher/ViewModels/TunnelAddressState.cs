namespace McServerLauncher.ViewModels;

/// <summary>
/// How far along the public Java address is for a server that uses a playit tunnel.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="BedrockAddressState"/>, and it should have been written at the same
/// time. The Java box had exactly the two states the Bedrock panel used to have — an address, or
/// nothing — and the same three situations all rendered as nothing: the tunnel exists but playit has
/// not published its domain yet, there is no tunnel on this port at all, and the lookup failed.
/// </para>
/// <para>
/// The first resolves itself in seconds, the second needs the user to press "Create tunnel", and the
/// third is worth retrying. An empty box says none of that, while the console has already announced
/// that the address "will appear shortly" — so the only reading left is that the app is broken.
/// </para>
/// </remarks>
public enum TunnelAddressState
{
    /// <summary>A tunnel is expected; playit has not published its address yet.</summary>
    Waiting,

    /// <summary>No tunnel was found on this port. Nothing will appear until one is made.</summary>
    NoTunnel,

    /// <summary>The address is known and shown in the box.</summary>
    Ready,

    /// <summary>The tunnel could not be looked up. Whatever was shown before is kept.</summary>
    Failed
}

/// <summary>The line of explanation each state shows.</summary>
public static class TunnelAddressStates
{
    /// <summary>The resx key describing <paramref name="state"/>.</summary>
    /// <remarks>
    /// A method rather than the switch written inline in the view model, because a key built at run
    /// time is invisible to the test that checks every key the code asks for exists: it only reads
    /// string literals. Here a test can walk the enum and prove all four resolve.
    /// </remarks>
    public static string KeyFor(TunnelAddressState state) => state switch
    {
        TunnelAddressState.Ready => "Tunnel_StateReady",
        TunnelAddressState.NoTunnel => "Tunnel_StateNoTunnel",
        TunnelAddressState.Failed => "Tunnel_StateFailed",
        _ => "Tunnel_StateWaiting"
    };
}
