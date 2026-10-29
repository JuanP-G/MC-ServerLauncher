using Avalonia.Threading;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>What <see cref="IdleShutdownController"/> needs to know about, and to be able to do to, its server.</summary>
/// <param name="Minutes">The configured idle limit; 0 switches the feature off.</param>
/// <param name="IsRunning">Whether the server is up.</param>
/// <param name="PlayerCount">How many players are connected.</param>
/// <param name="WokeAtUtc">When the server was last woken by a knock, if ever.</param>
/// <param name="ShowCountdown">Puts the countdown text on screen, or null to take it away.</param>
/// <param name="AnnounceStop">Tells the user the server is being stopped for being empty.</param>
/// <param name="StopAsync">Stops the server the normal way.</param>
/// <param name="ReportError">Says a stop failed.</param>
public sealed record IdleHost(
    Func<int> Minutes,
    Func<bool> IsRunning,
    Func<int> PlayerCount,
    Func<DateTime?> WokeAtUtc,
    Action<string?> ShowCountdown,
    Action<int> AnnounceStop,
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

    /// <summary>Set while the idle stop is running, so one timeout can't queue several stops.</summary>
    private bool _stopping;

    /// <param name="host">The server being watched.</param>
    /// <param name="utcNow">The clock. The real one unless a test supplies its own.</param>
    public IdleShutdownController(IdleHost host, Func<DateTime>? utcNow = null)
    {
        _host = host;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        // Built here, started when there is something to count: a timer that exists is inert.
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
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
        StopCountdown();
        _host.AnnounceStop(minutes);
        _ = StopBecauseIdleAsync();
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
        StopCountdown();
    }

    /// <summary>Stops the countdown timer for good.</summary>
    public void Dispose() => _countdownTimer.Stop();

    private void UpdateCountdown()
    {
        if (_emptySinceUtc is null) { StopCountdown(); return; }

        _host.ShowCountdown(string.Format(
            Localizer.Get("Idle_CountdownFmt"),
            IdlePolicy.FormatCountdown(IdlePolicy.IdleRemaining(_host.Minutes(), _emptySinceUtc.Value, _utcNow()))));
    }

    private void StopCountdown()
    {
        _countdownTimer.Stop();
        _host.ShowCountdown(null);
    }

    private async Task StopBecauseIdleAsync()
    {
        // Through the normal Stop, so an automatic shutdown saves the world exactly like a manual
        // one — the last thing anyone wants from an unattended stop is a missing backup.
        try { await _host.StopAsync(); }
        catch (Exception ex)
        {
            _host.ReportError(string.Format(Localizer.Get("Msg_ErrorFmt"), ErrorMessages.Describe(ex)));
        }
        finally
        {
            _stopping = false;
            _emptySinceUtc = null;
        }
    }
}
