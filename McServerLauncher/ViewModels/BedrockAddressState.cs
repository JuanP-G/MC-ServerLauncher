namespace McServerLauncher.ViewModels;

/// <summary>
/// How far along the Bedrock address is for a server that has crossplay switched on.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the panel used to have only two states — an address, or nothing at all — and
/// three completely different situations all rendered as nothing: the tunnel exists but playit has
/// not finished assigning it a domain and a public port yet; there is no tunnel and there never
/// will be one, because crossplay was switched on without a playit key; and the lookup failed.
/// </para>
/// <para>
/// The first resolves itself in seconds, the second means "works on your own network only" and
/// needs the user to do something, and the third is worth retrying. Showing an empty box for all
/// three is why "the Bedrock port never appears" was reported as one bug when it was three.
/// </para>
/// </remarks>
public enum BedrockAddressState
{
    /// <summary>A tunnel is expected; playit has not published its address yet.</summary>
    Waiting,

    /// <summary>No tunnel: Geyser is listening, but only reachable from this network.</summary>
    LocalOnly,

    /// <summary>The public host and port are known and shown.</summary>
    Ready,

    /// <summary>The tunnel could not be looked up. Whatever was shown before is kept.</summary>
    Failed
}

/// <summary>The line of explanation each state shows.</summary>
public static class BedrockAddressStates
{
    /// <summary>The resx key describing <paramref name="state"/>.</summary>
    /// <remarks>
    /// A method rather than the switch written inline in the view model, because a key built at run
    /// time is invisible to the test that checks every key the code asks for exists: it only reads
    /// string literals. Here a test can walk the enum and prove all four resolve.
    /// </remarks>
    public static string KeyFor(BedrockAddressState state) => state switch
    {
        BedrockAddressState.Ready => "Crossplay_StateReady",
        BedrockAddressState.LocalOnly => "Crossplay_StateLocalOnly",
        BedrockAddressState.Failed => "Crossplay_StateFailed",
        _ => "Crossplay_StateWaiting"
    };
}
