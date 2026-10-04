using Avalonia;
using McServerLauncher.Controls;

namespace McServerLauncher.Tests;

/// <summary>
/// The text that slides sideways under the pointer when it does not fit.
/// </summary>
[Collection("avalonia")]
public class MarqueeTextTests(AvaloniaFixture ui)
{
    private const string Long = "Java+Bedrock (paper) para amigos y gente de fuera del grupo";

    private static MarqueeText Laid(string text, double width)
    {
        var m = new MarqueeText { Text = text };
        m.Measure(new Size(width, 40));
        m.Arrange(new Rect(0, 0, width, 40));
        return m;
    }

    // --- whether anything is hidden ---

    [Fact]
    public void ALongTextInANarrowSpaceOverflows() =>
        ui.Run(() => Assert.True(Laid(Long, 100).IsOverflowing));

    [Fact]
    public void AShortTextInAWideSpaceDoesNot() =>
        ui.Run(() => Assert.False(Laid("Hola", 400).IsOverflowing));

    [Fact]
    public void TheWidthIsNeverMoreThanTheSpaceGiven() =>
        ui.Run(() =>
        {
            var m = Laid(Long, 100);
            Assert.True(m.DesiredSize.Width <= 100);
        });

    // --- sliding ---

    [Fact]
    public void ATextThatFitsNeverStartsToSlide() =>
        ui.Run(() =>
        {
            var m = Laid("Hola", 400);

            m.StartScroll();

            Assert.False(m.IsScrolling);
        });

    [Fact]
    public void AnOverflowingTextStartsAndStopsBackAtRest() =>
        ui.Run(() =>
        {
            var m = Laid(Long, 100);

            m.StartScroll();
            Assert.True(m.IsScrolling);

            m.StopScroll();
            Assert.False(m.IsScrolling);
            Assert.Equal(0, m.Offset);
        });

    [Fact]
    public void ChangingTheTextWhileSlidingPutsItBack() =>
        ui.Run(() =>
        {
            var m = Laid(Long, 100);
            m.StartScroll();

            m.Text = "Otro";

            Assert.False(m.IsScrolling);
        });

    // --- the timeline ---

    [Fact]
    public void ItRestsBeforeAndAfterTheSlide()
    {
        // 80 px to travel at 40 px/s is two seconds each way.
        var rest = MarqueeText.Pause;

        Assert.Equal(0, MarqueeText.OffsetAt(TimeSpan.Zero, 80));
        Assert.Equal(0, MarqueeText.OffsetAt(rest - TimeSpan.FromMilliseconds(1), 80));
        Assert.Equal(80, MarqueeText.OffsetAt(rest + TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1), 80));
    }

    [Fact]
    public void ItMovesAtASteadySpeed()
    {
        var start = MarqueeText.Pause;

        var a = MarqueeText.OffsetAt(start + TimeSpan.FromSeconds(0.5), 200);
        var b = MarqueeText.OffsetAt(start + TimeSpan.FromSeconds(1.5), 200);

        Assert.Equal(MarqueeText.Speed * 0.5, a, 6);
        Assert.Equal(MarqueeText.Speed * 1.5, b, 6);
    }

    [Fact]
    public void ItNeverGoesPastTheEndsAndComesBack()
    {
        var distance = 100.0;
        var travel = TimeSpan.FromSeconds(distance / MarqueeText.Speed);
        var back = MarqueeText.Pause + travel + MarqueeText.Pause + TimeSpan.FromSeconds(1);

        for (var ms = 0; ms < 20000; ms += 37)
        {
            var x = MarqueeText.OffsetAt(TimeSpan.FromMilliseconds(ms), distance);
            Assert.InRange(x, 0, distance);
        }

        // One second into the return it is one second of travel short of the far end.
        Assert.Equal(distance - MarqueeText.Speed, MarqueeText.OffsetAt(back, distance), 6);
    }

    [Fact]
    public void ItRepeatsWhileThePointerStaysOver()
    {
        var distance = 80.0;
        var cycle = TimeSpan.FromSeconds(2 * (MarqueeText.Pause.TotalSeconds + distance / MarqueeText.Speed));

        Assert.Equal(0, MarqueeText.OffsetAt(cycle, distance), 6);
        Assert.Equal(
            MarqueeText.OffsetAt(TimeSpan.FromSeconds(1.7), distance),
            MarqueeText.OffsetAt(TimeSpan.FromSeconds(1.7) + cycle, distance), 6);
    }

    [Fact]
    public void NothingToSlideMeansNoMovement() =>
        Assert.Equal(0, MarqueeText.OffsetAt(TimeSpan.FromSeconds(5), 0));
}

/// <summary>Which element's hover starts the slide.</summary>
[Collection("avalonia")]
public class MarqueeScopeTests(AvaloniaFixture ui)
{
    [Fact]
    public void AMarkedCardIsTheScopeNotTheTextAlone() =>
        ui.Run(() =>
        {
            // The server card: the name sits in a grid inside the card's border.
            var text = new MarqueeText { Text = "Java+Bedrock (paper) para amigos" };
            var card = new Avalonia.Controls.Border { Child = new Avalonia.Controls.Grid { Children = { text } } };
            MarqueeText.SetIsHoverScope(card, true);
            var window = new Avalonia.Controls.Window { Content = card };
            window.Show();

            Assert.Same(card, MarqueeText.FindScope(text));
            window.Hide();
        });

    [Fact]
    public void WithNothingMarkedTheTextIsItsOwnScope() =>
        ui.Run(() =>
        {
            var text = new MarqueeText { Text = "x" };
            var window = new Avalonia.Controls.Window { Content = new Avalonia.Controls.StackPanel { Children = { text } } };
            window.Show();

            Assert.Same(text, MarqueeText.FindScope(text));
            window.Hide();
        });

    [Fact]
    public void TheServerCardAndTheTunnelRowsAreMarked()
    {
        var views = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views");

        Assert.Contains("controls:MarqueeText.IsHoverScope=\"True\"", File.ReadAllText(Path.Combine(views, "ServerCardView.axaml")));
        Assert.Contains("controls:MarqueeText.IsHoverScope=\"True\"", File.ReadAllText(Path.Combine(views, "TunnelsView.axaml")));
    }
}
