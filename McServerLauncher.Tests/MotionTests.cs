using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The motion layer: that it lives in one place, that it stays fast, and that it can be taken out.
/// </summary>
/// <remarks>
/// None of these watch an animation run. A screenshot of something moving proves nothing, and the
/// headless clock does not advance on its own, so "does it look right" is the one thing here that
/// stays a human job. What these do protect is everything that would quietly rot: a transition
/// written into some view instead of the layer, a duration that creeps up until the app feels slow,
/// and a panel left clickable while invisible.
/// </remarks>
public class MotionTests
{
    private static string Layer() => Path.Combine(
        LocalizationTests.RepoRoot(), "McServerLauncher", "Styles", MotionSwitch.FileName);

    private static IEnumerable<string> EveryView() =>
        Directory.EnumerateFiles(
            Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher"), "*.axaml",
            SearchOption.AllDirectories);

    [Fact]
    public void EveryAnimationInTheAppLivesInTheOneLayer()
    {
        // This is what makes the setting a single line and the tempo tunable in one place. The
        // moment a view declares its own <Transitions>, turning animations off stops turning that
        // one off, and nobody finds out until somebody who needs them off complains.
        var strays = new List<string>();

        foreach (var file in EveryView())
        {
            if (Path.GetFileName(file) == MotionSwitch.FileName) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains("<Transitions", StringComparison.Ordinal) ||
                    lines[i].Contains("<Animation", StringComparison.Ordinal))
                    strays.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
        }

        Assert.True(strays.Count == 0,
            "El movimiento tiene que vivir en Styles/" + MotionSwitch.FileName +
            ", y estas vistas se lo han montado por su cuenta:\n  " + string.Join("\n  ", strays));
    }

    [Fact]
    public void NothingTakesLongerThanThreeTenthsOfASecond()
    {
        // The line between "responsive" and "sluggish" is not a matter of taste at this end of the
        // scale: past ~300 ms a transition stops reading as feedback and starts reading as the app
        // thinking. Written down so it cannot drift a few milliseconds at a time.
        var text = File.ReadAllText(Layer());
        var slow = new List<string>();

        foreach (Match m in Regex.Matches(text, @"\b0:0:(\d+(?:\.\d+)?)\b"))
        {
            var seconds = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            // The status dot's pulse is a loop, not a transition: it is meant to be slow, and a
            // heartbeat at 300 ms would look like a fault light.
            if (seconds > 0.3 && !LineOf(text, m.Index).Contains("IterationCount", StringComparison.Ordinal))
                slow.Add($"{seconds:0.###} s — {LineOf(text, m.Index).Trim()}");
        }

        Assert.True(slow.Count == 0, "Demasiado lento para ser respuesta:\n  " + string.Join("\n  ", slow));
    }

    private static string LineOf(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)];
    }

    [Fact]
    public void OnlyTheFourNamedSpeedsAreUsed()
    {
        // Four durations are a tempo. Seven are a pile, and then nobody knows which one to reach for.
        var doc = XDocument.Load(Layer());
        var declared = doc.Descendants()
            .Where(e => e.Name.LocalName == "TimeSpan")
            .Select(e => e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value)
            .Where(v => v is not null)
            .ToList();

        Assert.Equal(4, declared.Count);
        Assert.All(declared, d => Assert.StartsWith("Motion", d!, StringComparison.Ordinal));
    }

    [Fact]
    public void AnInvisibleSectionCannotBeClicked()
    {
        // The bug the cross-fade brings with it. Fading a section means keeping it mounted, and a
        // panel at zero opacity still takes clicks and still takes tab focus — so the pointer lands
        // on something that is not there. Whatever the opacity is bound to, IsEnabled has to be
        // bound to the matching flag.
        var doc = XDocument.Load(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml"));

        var sections = doc.Descendants()
            .Where(e => e.Attribute("Classes")?.Value.Split(' ').Contains("section") == true)
            .ToList();

        Assert.Equal(2, sections.Count);

        foreach (var s in sections)
        {
            var opacity = s.Attribute("Opacity")?.Value;
            var enabled = s.Attribute("IsEnabled")?.Value;

            Assert.NotNull(opacity);
            Assert.NotNull(enabled);

            // "{Binding ServersOpacity}" and "{Binding IsServersSection}" — the same section.
            var which = opacity!.Replace("{Binding ", "").Replace("Opacity}", "");
            Assert.Contains(which, enabled!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoSectionIsHiddenWithIsVisibleAnyMore()
    {
        // Belt and braces on the same bug from the other side: IsVisible="false" removes the element
        // from layout, so it cannot fade — it just vanishes, and the cross-fade silently becomes a
        // cut that still passes every other test here.
        var doc = XDocument.Load(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml"));

        var hidden = doc.Descendants()
            .Where(e => e.Attribute("Classes")?.Value.Split(' ').Contains("section") == true)
            .Where(e => e.Attribute("IsVisible") is not null)
            .ToList();

        Assert.Empty(hidden);
    }
}

/// <summary>The switch itself, against a real application.</summary>
[Collection("avalonia")]
public class MotionSwitchTests(AvaloniaFixture avalonia)
{
    [Fact]
    public void TurningItOffTakesTheTransitionsOffTheControlsThemselves()
    {
        // The claim the setting makes, checked rather than assumed: not "the dictionary is gone"
        // but "a button really has no transitions any more". Those are different statements, and
        // only the second one is what the person who switched it off asked for.
        avalonia.Run(() =>
        {
            var app = Application.Current!;

            Assert.True(MotionSwitch.IsOn(app.Styles));

            Assert.True(Sinks(FreshButton()));

            MotionSwitch.Apply(app.Styles, false);
            Assert.False(MotionSwitch.IsOn(app.Styles));
            Assert.False(Sinks(FreshButton()));

            // And back, because a setting you cannot undo is not a setting.
            MotionSwitch.Apply(app.Styles, true);
            Assert.True(MotionSwitch.IsOn(app.Styles));
            Assert.True(Sinks(FreshButton()));
        });
    }

    private static Button FreshButton()
    {
        var b = new Button();
        new Window { Content = b }.Show();
        AvaloniaFixture.Pump();
        return b;
    }

    /// <summary>Whether the motion layer's own transitions are on this button.</summary>
    /// <remarks>
    /// <para>
    /// It asks about the Opacity one and not about the transform, and that is not arbitrary.
    /// Measured: a Fluent button ALREADY carries a <c>TransformOperationsTransition</c> on
    /// <c>RenderTransform</c>, at 75 ms, with or without this layer — so "has a transform
    /// transition" is true either way and an earlier version of this test asserted nothing at all
    /// while looking perfectly reasonable. With the layer, that same transition reads 90 ms and an
    /// Opacity one appears next to it; the Opacity one is ours alone.
    /// </para>
    /// <para>
    /// Worth knowing for its own sake: Fluent shipped the transition but no <c>:pressed</c> setter
    /// to drive it, which is why pressing a button in this app never moved anything. The setter
    /// lives in the motion layer, so switching motion off removes it and nothing animates — the
    /// leftover Fluent transition has nothing left to interpolate.
    /// </para>
    /// </remarks>
    private static bool Sinks(Button b) =>
        b.Transitions?.Any(t => t is DoubleTransition { Property: var p } && p == Visual.OpacityProperty) == true;

    [Fact]
    public void TheLayerGoesBackWhereItWas()
    {
        // Re-added at the end it would have different precedence, and the app would look subtly
        // different after toggling the setting twice — which is exactly the kind of thing nobody
        // manages to report because nobody believes it.
        avalonia.Run(() =>
        {
            var app = Application.Current!;
            var before = IndexOfLayer(app.Styles);

            MotionSwitch.Apply(app.Styles, false);
            MotionSwitch.Apply(app.Styles, true);

            Assert.Equal(before, IndexOfLayer(app.Styles));
        });
    }

    // Misma pregunta que hace MotionSwitch, y por el mismo motivo: al cargar, Avalonia resuelve
    // el StyleInclude y no queda ninguna ruta contra la que comparar.
    private static int IndexOfLayer(Avalonia.Styling.Styles styles)
    {
        for (var i = 0; i < styles.Count; i++)
            if (styles[i] is Avalonia.Controls.IResourceProvider p &&
                p.TryGetResource("MotionPress", null, out _))
                return i;

        return -1;
    }
}
