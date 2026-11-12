using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The clock that stops an empty server, run against a fake server and a fake time: when it starts,
/// when a player resets it, and that it stops once and only once.
/// </summary>
/// <remarks>
/// The rules themselves are <see cref="McServerLauncher.Services.IdlePolicy"/>'s and are checked in
/// <c>IdleAndWakeTests</c>; what is pinned here is that the controller applies them in the right
/// order and keeps its own state straight.
/// </remarks>
[Collection("avalonia")]
public class IdleShutdownControllerTests(AvaloniaFixture ui)
{
    private sealed class FakeServer
    {
        public DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        public int Minutes = 10;
        public bool Running = true;
        public int Players;

        /// <summary>What the server answers when asked; null is no answer.</summary>
        public Func<Task<int?>> Ask = () => Task.FromResult<int?>(0);
        public int Asked;
        public readonly List<int> Held = new();
        public DateTime? WokeAtUtc;
        public string? Countdown;
        public readonly List<int> Announced = new();
        public int Stops;
        public readonly List<string> Errors = new();
        public Func<Task> StopBehaviour = () => Task.CompletedTask;

        public IdleShutdownController Controller() => new(new IdleHost(
            Minutes: () => Minutes,
            IsRunning: () => Running,
            PlayerCount: () => Players,
            AskServer: () => { Asked++; return Ask(); },
            WokeAtUtc: () => WokeAtUtc,
            ShowCountdown: text => Countdown = text,
            AnnounceStop: Announced.Add,
            AnnounceHeld: Held.Add,
            StopAsync: () => { Stops++; return StopBehaviour(); },
            ReportError: Errors.Add), () => Now);
    }

    [Fact]
    public void AnEmptyRunningServerStartsTheClockAndShowsTheCountdown()
    {
        ui.Run(() =>
        {
            var server = new FakeServer();
            using var idle = server.Controller();

            idle.Check();

            Assert.True(idle.IsCounting);
            Assert.NotNull(server.Countdown);
            Assert.Equal(0, server.Stops);
        });
    }

    [Fact]
    public void TurnedOffMeansNoClock()
    {
        ui.Run(() =>
        {
            var server = new FakeServer { Minutes = 0 };
            using var idle = server.Controller();

            idle.Check();

            Assert.False(idle.IsCounting);
            Assert.Null(server.Countdown);
        });
    }

    [Fact]
    public void ASomeoneOnTheServerKeepsItAlive()
    {
        ui.Run(() =>
        {
            var server = new FakeServer { Players = 1 };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromHours(3);
            idle.Check();

            Assert.False(idle.IsCounting);
            Assert.Equal(0, server.Stops);
        });
    }

    [Fact]
    public void OnceItHasBeenEmptyLongEnoughItIsAnnouncedAndStoppedExactlyOnce()
    {
        ui.Run(() =>
        {
            var pending = new TaskCompletionSource();
            var server = new FakeServer { StopBehaviour = () => pending.Task };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();
            idle.Check();   // a second tick while the stop is still being carried out: no second stop

            Assert.Equal([10], server.Announced);
            Assert.Equal(1, server.Stops);
            Assert.Null(server.Countdown);
        });
    }

    /// <summary>
    /// Issue #167 and its relatives: the console showed nobody joining, the server had people on.
    /// </summary>
    [Fact]
    public void AServerThatSaysItHasPlayersIsNotStoppedWhateverTheConsoleShowed()
    {
        ui.Run(() =>
        {
            var server = new FakeServer { Ask = () => Task.FromResult<int?>(3) };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();

            Assert.Equal(0, server.Stops);
            Assert.Empty(server.Announced);
            Assert.Equal([3], server.Held);
            Assert.True(idle.IsCounting);   // from now, for another full limit
        });
    }

    [Fact]
    public void ItIsAskedAgainOnlyAfterAnotherFullLimitAndToldOnce()
    {
        ui.Run(() =>
        {
            var server = new FakeServer { Ask = () => Task.FromResult<int?>(1) };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();
            server.Now += TimeSpan.FromMinutes(5);
            idle.Check();   // half-way through the new count: no question
            Assert.Equal(1, server.Asked);

            server.Now += TimeSpan.FromMinutes(5);
            idle.Check();

            Assert.Equal(2, server.Asked);
            Assert.Single(server.Held);   // said once, not every time
            Assert.Equal(0, server.Stops);
        });
    }

    [Fact]
    public void OnceTheServerSaysNobodyIsOnItIsStopped()
    {
        ui.Run(() =>
        {
            var online = 2;
            var server = new FakeServer { Ask = () => Task.FromResult<int?>(online) };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();
            online = 0;
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();

            Assert.Equal(1, server.Stops);
            Assert.Equal([10], server.Announced);
        });
    }

    /// <summary>No answer is not "somebody is on": the console's count stands, as it always did.</summary>
    [Fact]
    public void AServerThatDoesNotAnswerIsStoppedOnTheConsolesWord()
    {
        ui.Run(() =>
        {
            var server = new FakeServer { Ask = () => Task.FromResult<int?>(null) };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();

            Assert.Equal(1, server.Stops);
        });
    }

    [Fact]
    public void AQuestionThatThrowsCountsAsNoAnswer()
    {
        ui.Run(() =>
        {
            var server = new FakeServer { Ask = () => throw new InvalidOperationException("boom") };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();

            Assert.Equal(1, server.Stops);
            Assert.Empty(server.Errors);
        });
    }

    [Fact]
    public void WhileTheServerIsBeingAskedNothingElseIsAskedOrStopped()
    {
        ui.Run(() =>
        {
            var answer = new TaskCompletionSource<int?>();
            var server = new FakeServer { Ask = () => answer.Task };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();
            idle.Check();
            idle.Check();

            Assert.Equal(1, server.Asked);
            Assert.Equal(0, server.Stops);
        });
    }

    [Fact]
    public void ThePlayerWhoJoinsWhileTheServerIsBeingAskedKeepsItAlive()
    {
        ui.Run(() =>
        {
            var server = new FakeServer();
            server.Ask = () => { server.Players = 1; return Task.FromResult<int?>(0); };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();

            Assert.Equal(0, server.Stops);
            Assert.Empty(server.Announced);
        });
    }

    [Fact]
    public void ThePlayerWhoJoinsInTimeResetsTheClock()
    {
        ui.Run(() =>
        {
            var server = new FakeServer();
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(9);
            server.Players = 1;
            idle.Check();
            server.Players = 0;
            server.Now += TimeSpan.FromMinutes(5);
            idle.Check();   // empty again, but for five minutes, not fourteen

            Assert.Equal(0, server.Stops);
            Assert.True(idle.IsCounting);
        });
    }

    [Fact]
    public void AServerJustWokenByAKnockIsLeftAloneWhileItsPlayerConnects()
    {
        ui.Run(() =>
        {
            var server = new FakeServer();
            server.WokeAtUtc = server.Now;
            using var idle = server.Controller();

            server.Now += TimeSpan.FromMinutes(3);
            idle.Check();

            Assert.False(idle.IsCounting);
        });
    }

    [Fact]
    public void AStopThatFailsIsReportedAndTheClockCanRunAgain()
    {
        ui.Run(() =>
        {
            var server = new FakeServer { StopBehaviour = () => throw new InvalidOperationException("boom") };
            using var idle = server.Controller();

            idle.Check();
            server.Now += TimeSpan.FromMinutes(10);
            idle.Check();

            Assert.Single(server.Errors);
            Assert.False(idle.IsCounting);

            idle.Check();   // still empty and running: counted from scratch
            Assert.True(idle.IsCounting);
        });
    }

    [Fact]
    public void ResetTakesTheCountdownAway()
    {
        ui.Run(() =>
        {
            var server = new FakeServer();
            using var idle = server.Controller();
            idle.Check();

            idle.Reset();

            Assert.False(idle.IsCounting);
            Assert.Null(server.Countdown);
        });
    }
}
