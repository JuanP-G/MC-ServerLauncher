using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The rules that decide when a server stops itself and what a player sees while it sleeps.
/// </summary>
/// <remarks>
/// These are pure functions on purpose. Getting <see cref="ServerViewModel.ShouldCountIdle"/> wrong
/// does not produce a wrong pixel somewhere — it shuts down a server with people playing on it, so
/// every branch is pinned here rather than trusted.
/// </remarks>
public class IdleAndWakeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // --- when the clock runs at all ---

    [Fact]
    public void ZeroMinutesMeansNeverStop() =>
        Assert.False(ServerViewModel.ShouldCountIdle(0, running: true, playerCount: 0));

    [Fact]
    public void NeverCountsWithPlayersOn() =>
        Assert.False(ServerViewModel.ShouldCountIdle(30, running: true, playerCount: 1));

    [Fact]
    public void NeverCountsWhileStopped() =>
        Assert.False(ServerViewModel.ShouldCountIdle(30, running: false, playerCount: 0));

    [Fact]
    public void CountsWhenRunningAndEmpty() =>
        Assert.True(ServerViewModel.ShouldCountIdle(30, running: true, playerCount: 0));

    // --- when it has been long enough ---

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(90, true)]
    public void StopsOnlyOnceTheWaitHasElapsed(int minutesElapsed, bool expected) =>
        Assert.Equal(expected, ServerViewModel.IsIdleLongEnough(30, T0, T0.AddMinutes(minutesElapsed)));

    // --- the grace period after being woken ---

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(6, false)]
    public void FreshlyWokenServerIsProtected(int minutesAgo, bool expected) =>
        Assert.Equal(expected, ServerViewModel.IsWithinWakeGrace(T0.AddMinutes(-minutesAgo), T0));

    [Fact]
    public void GraceDoesNotApplyIfItNeverWoke() =>
        Assert.False(ServerViewModel.IsWithinWakeGrace(null, T0));

    // --- the countdown ---

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10, 20)]
    [InlineData(30, 0)]
    [InlineData(45, 0)]      // past the mark: clamped, never negative
    public void RemainingIsClampedAtZero(int elapsed, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            ServerViewModel.IdleRemaining(30, T0, T0.AddMinutes(elapsed)));

    [Theory]
    [InlineData(754, "12:34")]
    [InlineData(305, "5:05")]
    [InlineData(3723, "1:02:03")]
    [InlineData(0, "0:00")]
    public void CountdownIsFormattedForReading(int seconds, string expected) =>
        Assert.Equal(expected, ServerViewModel.FormatCountdown(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void CountdownRoundsUpSoItNeverLooksStuck()
    {
        // Truncating would park the display on 0:00 for up to a second while the server is still
        // perfectly alive, which reads as a broken clock rather than an imminent shutdown.
        Assert.Equal("0:01", ServerViewModel.FormatCountdown(TimeSpan.FromMilliseconds(400)));
        Assert.Equal("1:00", ServerViewModel.FormatCountdown(TimeSpan.FromSeconds(59.5)));
    }

    // --- the notice in the server list ---

    private const string Notice = "§r§e§lApagado";

    [Fact]
    public void NoticeGoesOnItsOwnLineUnderTheMotd() =>
        Assert.Equal("Mi servidor\nApagado".Replace("Apagado", Notice),
            WakeSign.Compose("Mi servidor", Notice));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void WithoutAMotdTheNoticeStandsAlone(string? motd) =>
        Assert.Equal(Notice, WakeSign.Compose(motd, Notice));

    [Theory]
    [InlineData("Linea uno\nLinea dos")]
    [InlineData("Linea uno\r\nLinea dos")]
    public void TwoLineMotdIsTrimmedSoTheNoticeSurvives(string motd)
    {
        // The server list shows two lines and no more. A MOTD already using both would push the
        // notice off the bottom — and the notice is the one line that has to be read.
        Assert.Equal("Linea uno\n" + Notice, WakeSign.Compose(motd, Notice));
    }

    [Fact]
    public void TheStoredFormIsTrimmedToo()
    {
        // The theory above passed for months while the thing it describes was broken for real
        // players, and this is exactly why: it feeds a real newline, and a real newline is what
        // never reaches here. server.properties is line-oriented and cannot hold one, so a two-line
        // sign is stored as the two characters backslash and n — and splitting on char 10 finds
        // nothing to split.
        //
        // What people actually saw: the whole sign survived as "line one", the notice went under
        // it, and anyone opening their server list while this server slept read a literal
        // backslash-n in the middle of the message. A test fed data the app never produces is not
        // a test, it is a comment that runs.
        Assert.Equal("Linea uno\n" + Notice,
            WakeSign.Compose(@"Linea uno\nLinea dos", Notice));
    }
}
