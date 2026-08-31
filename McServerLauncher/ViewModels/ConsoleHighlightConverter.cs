using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace McServerLauncher.ViewModels;

/// <summary>
/// A line plus what is being searched for, as text with the matches marked inside it.
/// </summary>
/// <remarks>
/// <para>
/// Filtering answers "which lines", and on a line four hundred characters long that is only half the
/// question. A stack frame buried in the middle of one is found by the filter and still has to be
/// hunted for by eye; marking it where it is turns finding into seeing.
/// </para>
/// <para>
/// Returns an <see cref="InlineCollection"/> bound to <c>TextBlock.Inlines</c>. That property is a
/// direct property in Avalonia 11.2.3 — checked against the assembly rather than assumed, because
/// the whole design rests on it — so it takes a binding like any other.
/// </para>
/// </remarks>
public class ConsoleHighlightConverter : IMultiValueConverter
{
    public static readonly ConsoleHighlightConverter Instance = new();

    /// <summary>The mark behind a match. Deliberately a background, not a colour change.</summary>
    /// <remarks>
    /// The text already carries a colour that means something — red for an error, blue for chat —
    /// and recolouring a match would destroy that meaning exactly where the user is looking hardest.
    /// A highlight behind the letters adds a signal instead of replacing one.
    /// </remarks>
    private static readonly IBrush MatchBackground = new SolidColorBrush(Color.Parse("#66E3A82B"));

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = values.Count > 0 ? values[0] as string ?? string.Empty : string.Empty;
        var term = values.Count > 1 ? values[1] as string : null;

        var inlines = new InlineCollection();

        if (string.IsNullOrWhiteSpace(term))
        {
            inlines.Add(new Run(text));
            return inlines;
        }

        var needle = term.Trim();
        var at = 0;

        while (at < text.Length)
        {
            var hit = text.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;

            if (hit > at) inlines.Add(new Run(text[at..hit]));
            inlines.Add(new Run(text.Substring(hit, needle.Length)) { Background = MatchBackground });

            at = hit + needle.Length;
        }

        if (at < text.Length) inlines.Add(new Run(text[at..]));

        // A search that matches nothing still has to render the line: it is reached whenever the
        // term matches somewhere else in the list, and returning nothing would blank the row.
        if (inlines.Count == 0) inlines.Add(new Run(text));

        return inlines;
    }
}
