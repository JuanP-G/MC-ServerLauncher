namespace McServerLauncher.ViewModels;

/// <summary>
/// How long to wait between the first few tunnel-address lookups, in seconds.
/// </summary>
/// <remarks>
/// <para>
/// Growing rather than fixed, and stopping rather than going on for ever: playit normally publishes
/// an address within a few seconds of the tunnel being created, and if it has not after about half
/// a minute then the reason is not one more request. The ordinary 30-second refresh takes over.
/// </para>
/// <para>
/// One definition for both addresses. The Java side had no burst at all and waited for that
/// 30-second refresh — which, with an arbitrary phase, left the box empty for anything up to a
/// minute after the console had already promised the address was seconds away.
/// </para>
/// </remarks>
internal static class AddressRetry
{
    /// <summary>The waits, in seconds, between one lookup and the next.</summary>
    internal static readonly int[] DelaysSeconds = { 2, 3, 5, 8, 13 };
}
