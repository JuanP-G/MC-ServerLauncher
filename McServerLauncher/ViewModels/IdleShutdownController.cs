using Avalonia.Threading;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>What <see cref="IdleShutdownController"/> needs to know about, and to be able to do to, its server.</summary>
/// <param name="Minutes">The configured idle limit; 0 switches the feature off.</param>
/// <param name="IsRunning">Whether the server is up.</param>
/// <param name="PlayerCount">How many players are connected, as far as the console showed.</param>
/// <param name="AskServer">
/// Asks the server itself how many players it has, the way a client's server list does. Null when it
/// did not say (status switched off, a port nothing answers on).
/// </param>
/// <param name="WokeAtUtc">When the server was last woken by a knock, if ever.</param>
/// <param name="ShowCountdown">Puts the countdown text on screen, or null to take it away.</param>
/// <param name="AnnounceStop">Tells the user the server is being stopped for being empty.</param>
/// <param name="AnnounceHeld">
/// Tells the user the stop was called off: the console showed nobody, and the server says it has
/// this many players.
/// </param>
/// <param name="StopAsync">Stops the server the normal way.</param>
/// <param name="ReportError">Says a stop failed.</param>
public sealed record IdleHost(
    Func<int> Minutes,
    Func<bool> IsRunning,
    Func<int> PlayerCount,
    Func<Task<int?>> AskServer,
    Func<DateTime?> WokeAtUtc,
    Action<string?> ShowCountdown,
    Action<int> AnnounceStop,
    Action<int> AnnounceHeld,
    Func<Task> StopAsync,
    Action<string> ReportError);

/// <summary>
/// Stops the server once it has been empty for the configured time, and shows the countdown while
/// that clock runs.
/// </summary>
/// <remarks>
/// <para>
/// The clock starts when the last player leaves — or when the server finishes starting, if nobody
/// ever joins — and is reset by anyone connecting, so a server in use is never stopped out from
/// under its players. The rules themselves are in <see cref="IdlePolicy"/>.
/// </para>
/// <para>
/// "Empty" is read from the console, and the console has been wrong before: a player's name the app
/// did not recognise, a plugin that hides the join message, a Minecraft version that reworded the
/// line — each time the list stayed empty and a server with people on it was stopped. So the last
/// word is the server's own. When the clock runs out it is asked how many players it has
/// (<see cref="IdleHost.AskServer"/>), and if it says anybody is on, nothing is stopped and the
/// clock starts again. Only when it says nobody, or does not answer at all, does the console's
/// count stand.
/// </para>
/// <para>
/// <see cref="Check"/> is called off the server's 2 s stats timer, which is the right cadence for
/// deciding when to stop but the wrong one for a clock: a seconds display driven by it would skip
/// every other second. The countdown therefore has its own one-second timer, which only runs while
/// there is something to count.
/// </para>
/// </remarks>
public sealed class IdleShutdownController : IDisposable
{
    private readonly IdleHost _host;
    private readonly Func<DateTime> _utcNow;
    private readonly DispatcherTimer _countdownTimer;

    /// <summary>Since when nobody has been connected, or null while someone is on.</summary>
    private DateTime? _emptySinceUtc;

    /// <summary>
    /// Set from the moment the clock runs out until the server has been asked and, if it really is
    /// empty, stopped: one timeout can't queue several questions or several stops.
    /// </summary>
    private bool _stopping;

    /// <summary>
    /// Whether the user has already been told, in this stretch, that the server contradicted the
    /// console. Once is enough: with a one-minute limit it would otherwise be a line a minute.
    /// </summary>
    private bool _heldAnnounced;

    /// <param name="host">The server being watched.</param>
    /// <param name="utcNow">The clock. The real one unless a test supplies its own.</param>
    public IdleShutdownController(IdleHost host, Func<DateTime>? utcNow = null)
    {
        _host = host;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        // Built here, started when there is something to count: a timer that exists is inert.
        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.UIThread) { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
    }

    /// <summary>Whether the clock is running, i.e. the server is empty and being counted down.</summary>
    public bool IsCounting => _emptySinceUtc is not null;

    /// <summary>Decides whether the clock runs, and stops the server when it has run out.</summary>
    public void Check()
    {
        if (_stopping) return;

        var now = _utcNow();
        var minutes = _host.Minutes();
        var counting = IdlePolicy.ShouldCountIdle(minutes, _host.IsRunning(), _host.PlayerCount())
                       && !IdlePolicy.IsWithinWakeGrace(_host.WokeAtUtc(), now);
        if (!counting)
        {
            Reset();
            return;
        }

        _emptySinceUtc ??= now;
        UpdateCountdown();
        _countdownTimer.Start();    // no-op when it is already running

        if (!IdlePolicy.IsIdleLongEnough(minutes, _emptySinceUtc.Value, now)) return;

        _stopping = true;
        _ = StopIfReallyEmptyAsync(minutes);
    }

    /// <summary>Forgets the clock and takes the countdown away.</summary>
    /// <remarks>
    /// Called explicitly when the server stops, not left to the next <see cref="Check"/>: that runs
    /// off the stats timer, which has just been stopped, so a server that went straight from Running
    /// to Stopped between two ticks would leave a countdown ticking away on a dead server.
    /// </remarks>
    public void Reset()
    {
        _emptySinceUtc = null;
        _heldAnnounced = false;
        StopCountdown();
    }

    /// <summary>Stops the countdown timer for good.</summary>
    public void Dispose() => _countdownTimer.Stop();

    private void UpdateCountdown()
    {
        if (_emptySinceUtc is null) { StopCountdown(); return; }

        _host.ShowCountdown(Localizer.Format(
            "Idle_CountdownFmt",
            IdlePolicy.FormatCountdown(IdlePolicy.IdleRemaining(_host.Minutes(), _emptySinceUtc.Value, _utcNow()))));
    }

    private void StopCountdown()
    {
        _countdownTimer.Stop();
        _host.ShowCountdown(null);
    }

    /// <summary>
    /// The clock ran out: asks the server, and stops it unless it says somebody is on.
    /// </summary>
    private async Task StopIfReallyEmptyAsync(int minutes)
    {
        int? online;
        try { online = await _host.AskServer(); }
        catch { online = null; }   // a question that failed is a question with no answer

        if (online is > 0)
        {
            // The console missed them. Start counting again rather than ask every two seconds, and
            // keep the countdown honest: it now runs from here.
            _emptySinceUtc = _utcNow();
            if (!_heldAnnounced)
            {
                _heldAnnounced = true;
                _host.AnnounceHeld(online.Value);
            }
            _stopping = false;
            return;
        }

        // It stopped, or somebody joined, while the question was in the air: nothing to stop.
        if (!IdlePolicy.ShouldCountIdle(_host.Minutes(), _host.IsRunning(), _host.PlayerCount()))
        {
            _stopping = false;
            return;
        }

        StopCountdown();
        _host.AnnounceStop(minutes);
        await StopBecauseIdleAsync();
    }

    private async Task StopBecauseIdleAsync()
    {
        // Through the normal Stop, so an automatic shutdown saves the world exactly like a manual
        // one — the last thing anyone wants from an unattended stop is a missing backup.
        try { await _host.StopAsync(); }
        catch (Exception ex)
        {
            _host.ReportError(Localizer.Format("Msg_ErrorFmt", ErrorMessages.Describe(ex)));
        }
        finally
        {
            _stopping = false;
            _emptySinceUtc = null;
        }
    }
}
