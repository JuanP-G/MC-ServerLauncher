namespace McServerLauncher.Services;

/// <summary>Where the "is there a newer version" question stands.</summary>
public enum UpdateCheckState
{
    /// <summary>Not asked yet.</summary>
    Unknown,

    /// <summary>Asking now.</summary>
    Checking,

    /// <summary>Asked, and this is the newest.</summary>
    UpToDate,

    /// <summary>Asked, and there is a newer one.</summary>
    Available,

    /// <summary>Could not ask: no connection, or GitHub unavailable.</summary>
    Failed,
}

/// <summary>
/// Turning the update service's answer into one of the three outcomes a person can be told about.
/// </summary>
/// <remarks>
/// <see cref="UpdateService.CheckAsync"/> returns null for "nothing newer" and throws when it
/// cannot ask, and the old code swallowed both the same way. That was fine for a banner that only
/// appears when there is news. A button someone presses has to say which of the two it was:
/// "you are up to date" and "I could not find out" are different answers, and answering the second
/// with the first is a lie by omission.
/// </remarks>
public static class UpdateCheck
{
    public static async Task<(UpdateCheckState State, UpdateService.UpdateInfo? Info)> RunAsync(
        Func<Task<UpdateService.UpdateInfo?>> ask)
    {
        try
        {
            var info = await ask();
            return info is null ? (UpdateCheckState.UpToDate, null) : (UpdateCheckState.Available, info);
        }
        catch
        {
            return (UpdateCheckState.Failed, null);
        }
    }
}
