using Avalonia.Controls;
using Avalonia.Styling;

namespace McServerLauncher.Services;

/// <summary>
/// Turns the app's animations on and off by adding or removing one style dictionary.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole implementation of the setting, and that is the point. Every transition and
/// every animation in the app lives in <c>Styles/Motion.axaml</c> and nowhere else, so switching
/// motion off is removing that one entry from <see cref="Avalonia.Application.Styles"/> — there is
/// no flag threaded through the views and no <c>if</c> next to each animated control to forget.
/// </para>
/// <para>
/// The removed dictionary is kept rather than rebuilt from its URI: re-parsing it would mean the
/// styles are re-applied as *different instances*, and it would make turning motion back on able to
/// fail (a bad URI) in a way that turning it off never can. Its position is kept too, because a
/// style added at the end is a style with different precedence, and "the app looks slightly
/// different after toggling a setting twice" is the kind of bug nobody manages to report.
/// </para>
/// </remarks>
public static class MotionSwitch
{
    /// <summary>The dictionary this switch takes out and puts back.</summary>
    public const string FileName = "Motion.axaml";

    private static IStyle? _removed;
    private static int _index = -1;

    /// <summary>Whether the motion layer is currently in the given style list.</summary>
    public static bool IsOn(Styles styles) => IndexOf(styles) >= 0;

    /// <summary>Adds or removes the motion layer, leaving everything else alone.</summary>
    public static void Apply(Styles styles, bool on)
    {
        if (on)
        {
            if (IndexOf(styles) >= 0 || _removed is null) return;
            // Back where it was, not at the end: see the remark above.
            var at = _index >= 0 && _index <= styles.Count ? _index : styles.Count;
            styles.Insert(at, _removed);
            return;
        }

        var found = IndexOf(styles);
        if (found < 0) return;

        _index = found;
        _removed = styles[found];
        styles.RemoveAt(found);
    }

    /// <summary>
    /// Finds the motion layer by a token only it defines.
    /// </summary>
    /// <remarks>
    /// Not by its file name, which was the obvious way and does not work: Avalonia RESOLVES a
    /// <c>StyleInclude</c> while loading, so by the time the app is running
    /// <c>Application.Styles</c> holds plain <c>Styles</c> objects and there is no source URI left
    /// to match on. Measured, not guessed — the list reads FluentTheme, Styles, Styles, Style, and
    /// nothing in it remembers where the middle two came from.
    ///
    /// So it asks the only question that still has an answer: which dictionary defines
    /// <c>MotionPress</c>. That token exists for the transitions to use; identifying the layer by it
    /// costs nothing and cannot drift out of sync, because a motion layer without it would not work
    /// anyway.
    /// </remarks>
    private static int IndexOf(Styles styles)
    {
        for (var i = 0; i < styles.Count; i++)
            if (styles[i] is IResourceProvider provider &&
                provider.TryGetResource(Marker, null, out _))
                return i;

        return -1;
    }

    /// <summary>The token that says "this is the motion layer". See <see cref="IndexOf"/>.</summary>
    private const string Marker = "MotionPress";
}
