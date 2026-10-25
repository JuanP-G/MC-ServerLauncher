using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The clock of a toast: how long it stays, what is left, and how solid it looks.
/// </summary>
/// <remarks>
/// The window only draws these numbers, so they are checked here without one. What a person sees —
/// a bar that shrinks, a card that stops while the pointer is on it — is the numbers behaving.
/// </remarks>
public class ToastLifetimeTests
{
    [Fact]
    public void SomethingBrokenStaysLongerThanTheRest()
    {
        Assert.Equal(ToastLifetime.Serious, new ToastLifetime(NotificationLevel.Error).Duration);

        foreach (var level in new[] { NotificationLevel.Info, NotificationLevel.Success, NotificationLevel.Warning })
            Assert.Equal(ToastLifetime.Normal, new ToastLifetime(level).Duration);

        Assert.True(ToastLifetime.Serious > ToastLifetime.Normal);
    }

    [Fact]
    public void ItStartsFullAndInvisible()
    {
        var life = new ToastLifetime(NotificationLevel.Info);

        Assert.Equal(1, life.Remaining);
        Assert.Equal(0, life.Opacity);
        Assert.False(life.Expired);
    }

    [Fact]
    public void TheBarEmptiesAsTheTimeGoes()
    {
        var life = new ToastLifetime(NotificationLevel.Info);

        life.Advance(life.Duration / 2);
        Assert.Equal(0.5, life.Remaining, 3);

        life.Advance(life.Duration);   // far past the end
        Assert.Equal(0, life.Remaining);
        Assert.True(life.Expired);
    }

    [Fact]
    public void ItFadesInOverTheFirstMomentsAndOutOverTheLast()
    {
        var life = new ToastLifetime(NotificationLevel.Info);

        life.Advance(ToastLifetime.Fade / 2);
        Assert.Equal(0.5, life.Opacity, 3);

        life.Advance(life.Duration / 2);
        Assert.Equal(1, life.Opacity);

        life.Advance(life.Duration);
        Assert.Equal(0, life.Opacity);
    }

    [Fact]
    public void WhilePausedTheClockDoesNotMove()
    {
        var life = new ToastLifetime(NotificationLevel.Info);
        life.Advance(TimeSpan.FromSeconds(2));

        life.Paused = true;
        life.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(2), life.Elapsed);
        Assert.False(life.Expired);

        life.Paused = false;
        life.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(3), life.Elapsed);
    }

    [Fact]
    public void PausingWhileAlreadyFadingOutBringsTheCardBack()
    {
        // The pointer arriving on a card that is nearly gone must not leave it stopped half
        // transparent: it steps back to where the fade-out begins, so it is solid again.
        var life = new ToastLifetime(NotificationLevel.Info);
        life.Advance(life.Duration - ToastLifetime.Fade / 3);
        Assert.True(life.Opacity < 0.5);

        life.Paused = true;

        Assert.Equal(1, life.Opacity);
        Assert.False(life.Expired);
    }

    [Fact]
    public void ANegativeOrZeroStepChangesNothing()
    {
        var life = new ToastLifetime(NotificationLevel.Info);
        life.Advance(TimeSpan.FromSeconds(1));

        life.Advance(TimeSpan.Zero);
        life.Advance(TimeSpan.FromSeconds(-5));

        Assert.Equal(TimeSpan.FromSeconds(1), life.Elapsed);
    }
}
