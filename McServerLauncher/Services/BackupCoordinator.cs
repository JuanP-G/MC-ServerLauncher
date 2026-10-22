using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What <see cref="BackupCoordinator"/> needs to know about, and ask of, the server it guards.
/// </summary>
internal interface IBackupHost
{
    ServerConfig Config { get; }

    /// <summary>The JVM exists: starting, running or stopping.</summary>
    bool IsProcessRunning { get; }

    /// <summary>A start is under way but has not launched the JVM yet.</summary>
    bool IsPreparing { get; }

    int PlayersOnline { get; }

    /// <summary>Writes a command to the server's console.</summary>
    void SendToConsole(string command);

    /// <summary>Shows a line from the launcher in the server's console.</summary>
    void Say(string line);
}

/// <summary>How a backup asked for by hand or by the clock went.</summary>
internal enum BackupResult
{
    /// <summary>A new zip is in the list.</summary>
    Made,

    /// <summary>There is no world yet: the server has never been started.</summary>
    NoWorld,

    /// <summary>Something else was reading or replacing the world, so nothing was done.</summary>
    Busy
}

/// <summary>How a restore went.</summary>
internal enum RestoreResult
{
    Restored,

    /// <summary>A backup was reading the world.</summary>
    Busy,

    /// <summary>The server started, or began to, after the user was asked.</summary>
    ServerRunning
}

/// <summary>
/// Everything that reads or replaces a server's world — backups, restores, and the moments the JVM
/// starts and stops writing to it — and the order they happen in.
/// </summary>
/// <remarks>
/// <para>
/// All of this used to live in the server's view model as a backup gate plus a handful of flags,
/// and each command took the gate only for the part it thought of as "the backup". So Stop sent
/// <c>stop</c> while a backup by the clock was still zipping: the server wrote the world on its way
/// down into a copy being read, the stop's own backup was refused because the gate was busy, and
/// that torn zip was then recorded as matching the world, so the next start did not copy it either.
/// A restore confirmed after a wake-on-demand knock had started the server replaced the world under
/// a running JVM.
/// </para>
/// <para>
/// The rule now has one place: <b>whatever reads or replaces the world, or makes the JVM start or
/// stop writing to it, holds <see cref="_world"/></b>. A start holds it from its backup until the
/// process is launched, a stop from <c>stop</c> until its own backup is made, and a restore asks
/// again, once inside, whether the server is still stopped.
/// </para>
/// <para>
/// Everything is called on the UI thread, except <see cref="ServerSaved"/>, which only completes a
/// task. No UI types: the host is an interface, so the protocol is tested without Avalonia.
/// </para>
/// </remarks>
internal sealed class BackupCoordinator
{
    private readonly IBackupHost _host;
    private readonly WorldBackupService _service;

    /// <summary>Held by whatever reads or replaces the world. See the class remarks.</summary>
    private readonly SemaphoreSlim _world = new(1, 1);

    /// <summary>
    /// Whether the world may have changed since the last backup: somebody joined, or a command that
    /// can change it was typed. See <see cref="BackupSchedule"/>.
    /// </summary>
    /// <remarks>
    /// Starts true, and goes back to true after every stop: until a start has checked the world
    /// against its backups, nobody knows what happened to it — it may have been edited by hand, or
    /// swapped for another. Only a start that made its backup, or found it up to date, clears it.
    /// </remarks>
    private bool _playedSinceBackup = true;

    /// <summary>The note a stop the app did not ask for left running; a start waits for it.</summary>
    private Task _noteUntouched = Task.CompletedTask;

    /// <summary>When the clock last made — or deliberately skipped — an automatic backup.</summary>
    private DateTime _lastAutoBackupUtc;

    /// <summary>Armed while a live backup is waiting for the server to confirm it has saved.</summary>
    private volatile TaskCompletionSource<bool>? _saveConfirmed;

    /// <summary>The copy in progress and why it was made, for a stop or a closing app to cut.</summary>
    private CancellationTokenSource? _copying;
    private string? _copyingTrigger;

    /// <summary>A stop holds the world and notes it itself; the Stopped event must not wait.</summary>
    private bool _stopHolding;

    internal BackupCoordinator(IBackupHost host, WorldBackupService service)
    {
        _host = host;
        _service = service;
    }

    private ServerConfig Config => _host.Config;

    /// <summary>True while a backup is being put back in place of the world.</summary>
    internal bool IsRestoring { get; private set; }

    /// <summary>Raised, synchronously, when <see cref="IsRestoring"/> changes.</summary>
    internal event Action? RestoringChanged;

    /// <summary>A copy of the world began (true) or ended (false). Lag during one is the copy's.</summary>
    /// <remarks>Raised on whatever thread runs the copy.</remarks>
    internal event Action<bool>? CopyingChanged;

    /// <summary>For tests and the docs: whether the world counts as changed since the last backup.</summary>
    internal bool PlayedSinceBackup => _playedSinceBackup;

    // ----- What the server tells it ---------------------------------------------------------------

    /// <summary>Somebody joined, or a command that changes the world was typed.</summary>
    internal void NoteWorldTouched() => _playedSinceBackup = true;

    /// <summary>The server said "Saved the game": the answer a live backup waits for. Any thread.</summary>
    internal void ServerSaved() => _saveConfirmed?.TrySetResult(true);

    /// <summary>
    /// The server stopped. Notes the world as untouched if nobody played — unless a stop the app
    /// asked for is doing that already.
    /// </summary>
    internal void ServerStopped()
    {
        if (_stopHolding) return;
        _noteUntouched = NoteUntouchedAsync();
    }

    // ----- The clock ------------------------------------------------------------------------------

    /// <summary>The server is up: the interval starts now, the start's backup covering the world.</summary>
    internal void ClockStarted(DateTime nowUtc) => _lastAutoBackupUtc = nowUtc;

    /// <summary>Once a minute while the server runs: is an automatic backup due?</summary>
    /// <remarks>
    /// Moves the clock on when one is due or skipped, so "nobody played" is not asked again in a
    /// minute. Says <see cref="BackupDue.NotYet"/> while a stop holds the world: the stop's own
    /// backup is about to cover it.
    /// </remarks>
    internal BackupDue ClockTick(DateTime nowUtc)
    {
        // Joining sets it too; this catches anybody already on when the server started counting.
        if (_host.PlayersOnline > 0) _playedSinceBackup = true;
        if (_stopHolding) return BackupDue.NotYet;

        var enabled = Config.BackupsEnabled && Config.AutoBackupEnabled;
        var due = BackupSchedule.Due(nowUtc, _lastAutoBackupUtc, Config.BackupIntervalMinutes,
            _playedSinceBackup, enabled);
        if (due != BackupDue.NotYet) _lastAutoBackupUtc = nowUtc;
        return due;
    }

    /// <summary>
    /// When the clock will next consider an automatic backup, while the server runs and they are
    /// on; null otherwise. "Consider": if nobody has played by then, it skips and waits another
    /// interval.
    /// </summary>
    internal DateTime? NextAutoBackupUtc(bool running) =>
        running && Config.BackupsEnabled && Config.AutoBackupEnabled
            ? _lastAutoBackupUtc + TimeSpan.FromMinutes(BackupSchedule.Clamp(Config.BackupIntervalMinutes))
            : null;

    // ----- Operations -----------------------------------------------------------------------------

    /// <summary>A backup asked for by hand or by the clock; refused, not queued, if busy.</summary>
    /// <remarks>
    /// The reason to ask twice is impatience, and answering it with a second copy of a world that is
    /// already being copied would only make the first one slower.
    /// </remarks>
    internal async Task<BackupResult> BackupAsync(string trigger, CancellationToken ct = default)
    {
        if (!await _world.WaitAsync(0, ct))
        {
            _host.Say(Localizer.Get("Msg_BackupAlreadyRunning"));
            return BackupResult.Busy;
        }

        try
        {
            return await CopyAsync(trigger, ct) is null ? BackupResult.NoWorld : BackupResult.Made;
        }
        finally
        {
            _world.Release();
        }
    }

    /// <summary>
    /// The start's part: waits for the world, backs it up unless it is still the last backup's, and
    /// calls <paramref name="launch"/> while still holding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Launching inside the hold is what keeps a copy from slipping in between "the world is backed
    /// up" and "the JVM has it open".
    /// </para>
    /// <para>
    /// A backup that fails does not keep the server down: it says so and launches anyway, counting
    /// the world as changed so the next chance makes the copy. It used to throw out of the start, so
    /// a disk that was full or a file an antivirus held left a server woken on demand, or restarting
    /// after a crash, down with nobody there to see why.
    /// </para>
    /// </remarks>
    internal async Task StartAsync(Action launch)
    {
        await _noteUntouched;   // a crash's restart: the note of that stop must be in before asking
        await TakeWorldAsync("Msg_BackupAlreadyRunning");
        try
        {
            if (Config.BackupsEnabled)
            {
                try
                {
                    _playedSinceBackup = !await BackupUnlessUnchangedAsync("start");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _host.Say(string.Format(Localizer.Get("Msg_BackupFailedStartingAnywayFmt"),
                        ErrorMessages.Describe(ex)));
                    _playedSinceBackup = true;
                }
            }

            launch();
        }
        finally
        {
            _world.Release();
        }
    }

    /// <summary>
    /// Stops the server holding the world: waits for a copy in progress, sends the stop, notes an
    /// untouched world and, when <paramref name="backupAfter"/>, makes the stop's backup.
    /// </summary>
    /// <remarks>
    /// A copy the clock started is cut short rather than waited for, since the stop's own backup,
    /// made right after, covers the same world; one somebody asked for by hand is waited for.
    /// </remarks>
    /// <param name="stopProcess">Sends "stop" and waits for the JVM to exit.</param>
    /// <param name="backupAfter">
    /// True for Stop and the idle stop; false for Restart, whose start makes the backup.
    /// </param>
    internal async Task StopAsync(Func<Task> stopProcess, bool backupAfter)
    {
        if (_copyingTrigger == "auto") _copying?.Cancel();
        await TakeWorldAsync("Msg_StopWaitsForBackup");
        _stopHolding = true;
        try
        {
            await stopProcess();
            await NoteInsideAsync();
            if (backupAfter && Config.BackupsEnabled) await BackupUnlessUnchangedAsync("stop");
        }
        finally
        {
            _stopHolding = false;
            _world.Release();
        }
    }

    /// <summary>
    /// The app is closing, or the server is being forgotten: cuts a copy short and stops the server
    /// (<paramref name="stopProcess"/>, when it runs) holding the world.
    /// </summary>
    /// <remarks>
    /// Only the copy in progress is cancelled, never anything after: a failed update brings the app
    /// back with these same servers, and they must still be able to back up.
    /// </remarks>
    internal async Task ShutdownAsync(Func<Task>? stopProcess)
    {
        _copying?.Cancel();
        await _world.WaitAsync();
        _stopHolding = true;
        try
        {
            if (stopProcess is null) return;
            await stopProcess();
            await NoteInsideAsync();
        }
        finally
        {
            _stopHolding = false;
            _world.Release();
        }
    }

    /// <summary>Replaces the world with a backup, if the server is still stopped.</summary>
    /// <remarks>
    /// The user was asked first, in a dialog that can stay open for as long as they like; a knock on
    /// a server that wakes on demand, or a restart after a crash, can start it meanwhile. So whether
    /// it is stopped is asked again here, inside the hold, on the same turn as the restore begins.
    /// </remarks>
    internal async Task<RestoreResult> RestoreAsync(string zipPath, IProgress<string>? log = null)
    {
        if (!await _world.WaitAsync(0)) return RestoreResult.Busy;
        try
        {
            if (_host.IsProcessRunning || _host.IsPreparing) return RestoreResult.ServerRunning;

            SetRestoring(true);
            try
            {
                await _service.RestoreBackupAsync(Config, zipPath, log);
                return RestoreResult.Restored;
            }
            finally
            {
                SetRestoring(false);
            }
        }
        finally
        {
            _world.Release();
        }
    }

    // ----- Inside the hold ------------------------------------------------------------------------

    /// <summary>Takes the world, saying why when it has to wait.</summary>
    private async Task TakeWorldAsync(string waitingKey)
    {
        if (await _world.WaitAsync(0)) return;
        _host.Say(Localizer.Get(waitingKey));
        await _world.WaitAsync();
    }

    /// <summary>
    /// The start's and the stop's backup: made only if the world is no longer the last backup's.
    /// True when the world is covered — copied now, or found already copied.
    /// </summary>
    private async Task<bool> BackupUnlessUnchangedAsync(string trigger)
    {
        var config = Config;
        if (await Task.Run(() => _service.IsUpToDate(config)))
        {
            _host.Say(Localizer.Get("Msg_BackupSkippedUnchanged"));
            return true;
        }
        return await CopyAsync(trigger, default) is not null;
    }

    /// <summary>The copy itself: live while the JVM runs, straight from the disk otherwise.</summary>
    private async Task<string?> CopyAsync(string trigger, CancellationToken ct)
    {
        using var copying = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _copying = copying;
        _copyingTrigger = trigger;
        CopyingChanged?.Invoke(true);
        try
        {
            var log = new Progress<string>(_host.Say);
            if (!_host.IsProcessRunning)
                return await _service.CreateBackupAsync(Config, trigger, log, copying.Token);

            // Cleared before the copy rather than after, so that somebody joining while it zips
            // still counts; anybody already on is still playing, so it is not cleared for them.
            var wasPlayed = _playedSinceBackup;
            _playedSinceBackup = _host.PlayersOnline > 0;
            string? zip = null;
            try
            {
                zip = await LiveWorldBackup.RunAsync(_service, Config, trigger, _host.SendToConsole,
                    timeout => WaitForSaveAsync(timeout, copying.Token), log, copying.Token);
                return zip;
            }
            finally
            {
                if (zip is null) _playedSinceBackup |= wasPlayed;   // nothing was copied: nothing is covered
            }
        }
        finally
        {
            _copying = null;
            _copyingTrigger = null;
            CopyingChanged?.Invoke(false);
        }
    }

    /// <summary>A stop nobody asked for (a crash, "stop" typed): notes the world once it is free.</summary>
    private async Task NoteUntouchedAsync()
    {
        await _world.WaitAsync();
        try
        {
            await NoteInsideAsync();
        }
        finally
        {
            _world.Release();
        }
    }

    /// <summary>
    /// After a stop: if nobody played and nothing was typed that changes the world, records the
    /// world as it is now as still being the last backup's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server writes on its way down even with nobody on — <c>level.dat</c> keeps when it was
    /// last saved — so without this the next start would find the files changed and copy them again.
    /// </para>
    /// <para>
    /// Safe to call twice: the first call takes the flag, a later one finds it set. Does nothing if
    /// a JVM is running again, which a late note must never fingerprint.
    /// </para>
    /// </remarks>
    private async Task NoteInsideAsync()
    {
        if (_playedSinceBackup || !Config.BackupsEnabled || _host.IsProcessRunning) return;

        _playedSinceBackup = true;
        var config = Config;
        await Task.Run(() => _service.MarkUnchanged(config));
    }

    /// <summary>
    /// Waits for the server to say it has saved, or gives up after <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// Arms the listener synchronously, before its first await, so that a caller can start the wait
    /// and only then send the command it is waiting for — which is the order that cannot miss an
    /// answer from a world small enough to flush instantly.
    /// </remarks>
    private async Task<bool> WaitForSaveAsync(TimeSpan timeout, CancellationToken ct)
    {
        var confirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _saveConfirmed = confirmed;
        try
        {
            return await Task.WhenAny(confirmed.Task, Task.Delay(timeout, ct)) == confirmed.Task;
        }
        finally
        {
            _saveConfirmed = null;
        }
    }

    private void SetRestoring(bool value)
    {
        IsRestoring = value;
        RestoringChanged?.Invoke();
    }
}
