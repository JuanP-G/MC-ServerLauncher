using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.ViewModels;

/// <summary>
/// One category of console line, as a toggle with a count behind it.
/// </summary>
/// <remarks>
/// <para>
/// The count is what makes these worth having. A row of on/off switches only helps once you already
/// know what you are looking for; "Errors 3" tells you there is something to look at <em>before</em>
/// you press anything, which is the difference between a filter you search with and one you watch
/// with.
/// </para>
/// <para>
/// <see cref="HasUnseen"/> covers the case that follows from it: an error arriving while its own
/// category is switched off would otherwise be completely invisible — filtered out of the list and
/// with nothing on screen changing. The switch says so instead.
/// </para>
/// </remarks>
public partial class ConsoleKindFilter : ObservableObject
{
    public ConsoleKindFilter(ConsoleLineKind kind)
    {
        Kind = kind;
        Label = Localizer.Get("Console_Kind" + kind);
    }

    /// <summary>The category this switch turns on and off.</summary>
    public ConsoleLineKind Kind { get; }

    /// <summary>Its name, in the user's language.</summary>
    public string Label { get; }

    /// <summary>Whether lines of this kind are shown. Everything starts on.</summary>
    [ObservableProperty]
    private bool _isOn = true;

    /// <summary>How many lines of this kind are in the buffer.</summary>
    [ObservableProperty]
    private int _count;

    /// <summary>True when lines of this kind arrived while it was switched off.</summary>
    [ObservableProperty]
    private bool _hasUnseen;

    /// <summary>The colour lines of this kind are drawn in, so the switch matches them.</summary>
    [ObservableProperty]
    private IBrush? _brush;

    partial void OnIsOnChanged(bool value)
    {
        // Turning a category back on is the user looking at it: whatever arrived while it was off
        // is no longer unseen.
        if (value) HasUnseen = false;
    }

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Tip));

    /// <summary>The tooltip: what this is, and how much of it there is.</summary>
    public string Tip => string.Format(Localizer.Get("Console_KindTipFmt"), Label, Count);

    /// <summary>
    /// Whether a line survives the search box and the category switches.
    /// </summary>
    /// <remarks>
    /// Both conditions, and the category first: it is a dictionary lookup against a line that is
    /// already in hand, while the search is a substring scan over text that can be hundreds of
    /// characters long. On two thousand lines rebuilt on every keystroke, the order is the
    /// difference between a filter that keeps up with typing and one that does not.
    /// </remarks>
    public static bool Matches(ConsoleLine line, string? term,
        IReadOnlyDictionary<ConsoleLineKind, ConsoleKindFilter> kinds)
    {
        if (kinds.TryGetValue(line.Kind, out var kind) && !kind.IsOn) return false;

        return string.IsNullOrWhiteSpace(term)
               || line.Text.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Counts every kind from scratch.
    /// </summary>
    /// <remarks>
    /// Used after the buffer is trimmed rather than trying to decrement as lines fall off the top.
    /// Two thousand lines is nothing, it happens once every couple of hundred lines, and it cannot
    /// drift — which a pair of counters maintained by hand at both ends certainly would, and the
    /// symptom would be a switch quietly claiming there are no errors.
    /// </remarks>
    public static void Recount(IEnumerable<ConsoleLine> lines, IEnumerable<ConsoleKindFilter> filters)
    {
        var byKind = filters.ToDictionary(f => f.Kind);
        foreach (var filter in byKind.Values) filter.Count = 0;

        foreach (var line in lines)
            if (byKind.TryGetValue(line.Kind, out var filter))
                filter.Count++;
    }
}
