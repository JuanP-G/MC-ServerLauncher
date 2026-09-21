using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Backs up the world of a server that is running, by asking Minecraft to let go of it first.
/// </summary>
/// <remarks>
/// <para>
/// Zipping a world the JVM is writing to gives a torn copy: some region files from before a save
/// and some from after, which is exactly the backup that looks fine until the day it is needed.
/// The way round it is the one every server host uses — stop saving, flush what is pending, wait
/// for the server to say it is done, copy, start saving again:
/// </para>
/// <code>
/// save-off
/// save-all flush
/// (wait for "Saved the game")
/// zip
/// save-on
/// </code>
/// <para>
/// <c>save-on</c> is in a <c>finally</c>, and that is the whole reason this is a separate class with
/// its commands handed in: it can be tested that the server is never left with saving switched off.
/// A backup that fails is an annoyance; a server that silently stops saving until somebody restarts
/// it loses everything played in between.
/// </para>
/// </remarks>
public static class LiveWorldBackup
{
    /// <summary>How long to wait for the server to confirm the flush before copying anyway.</summary>
    public static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs the sequence above and returns the new zip's path, or null if there was nothing to copy.</summary>
    /// <param name="service">Does the zipping and the pruning once the world is safe to read.</param>
    /// <param name="config">The server whose world is being copied.</param>
    /// <param name="trigger">Why this backup is being made; it ends up in the file's name.</param>
    /// <param name="sendCommand">Writes a line to the server's console.</param>
    /// <param name="waitForSave">
    /// Starts listening for the server's confirmation and returns the wait as a task. It is called
    /// <em>before</em> the flush command is sent, and awaited after: a small world can answer in the
    /// time between the two, and a listener started afterwards would miss it and wait out the whole
    /// timeout for a save that already happened.
    /// </param>
    /// <param name="log">Where the steps are reported, which is the server's own console.</param>
    /// <param name="ct">Cancels the copy. <c>save-on</c> is still sent.</param>
    public static async Task<string?> RunAsync(WorldBackupService service, ServerConfig config, string trigger,
        Action<string> sendCommand, Func<TimeSpan, Task<bool>> waitForSave, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        log?.Report(Localizer.Get("Msg_BackupPausingSaves"));
        sendCommand("save-off");
        try
        {
            var saved = waitForSave(SaveTimeout);
            sendCommand("save-all flush");

            if (!await saved)
                log?.Report(Localizer.Get("Msg_BackupSaveNotConfirmed"));

            return await service.CreateBackupAsync(config, trigger, log, ct);
        }
        finally
        {
            // Not conditional on anything, including cancellation: leaving a server with saving
            // switched off is worse than any outcome this method can have.
            sendCommand("save-on");
            log?.Report(Localizer.Get("Msg_BackupSavesResumed"));
        }
    }
}
