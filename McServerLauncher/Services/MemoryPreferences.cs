namespace McServerLauncher.Services;

/// <summary>
/// The app-wide memory settings the start path reads. Set at startup from the saved settings and
/// again when they change, like <see cref="WindowBehavior"/>.
/// </summary>
public static class MemoryPreferences
{
    /// <summary>The machine is only for servers, so they may share everything but 2 GB.</summary>
    public static bool DedicatedMachine { get; set; }
}
