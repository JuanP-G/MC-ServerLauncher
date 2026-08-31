using System;
using System.Collections.Generic;
using Avalonia.Media;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The console colours, as something Avalonia can draw with.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="ConsoleColors"/>, split from it exactly as
/// <c>NotificationBrushes</c> is split from <c>NotificationPalette</c>: the strings are a setting and
/// belong somewhere that knows nothing about a UI framework, the conversion belongs somewhere a UI
/// can reach.
/// </remarks>
public static class ConsolePalette
{
    /// <summary>The brush a kind is drawn in. Never null, never throws.</summary>
    /// <remarks>
    /// The hex has already been vetted for shape, but the framework is the last word on whether it
    /// can be drawn — and this runs while painting a line that might be reporting a crash.
    /// </remarks>
    public static IBrush BrushFor(ConsoleLineKind kind, NotificationSettings? levels,
        string? chat, string? players)
    {
        var hex = ConsoleColors.HexFor(kind, levels, chat, players);

        return Color.TryParse(hex, out var colour)
            ? new SolidColorBrush(colour)
            : new SolidColorBrush(Color.Parse(ConsoleColors.Info));
    }

    /// <summary>Every kind with its brush, for painting the filter switches to match the lines.</summary>
    public static IReadOnlyDictionary<ConsoleLineKind, IBrush> All(NotificationSettings? levels,
        string? chat, string? players)
    {
        var map = new Dictionary<ConsoleLineKind, IBrush>();
        foreach (var kind in Enum.GetValues<ConsoleLineKind>())
            map[kind] = BrushFor(kind, levels, chat, players);
        return map;
    }
}
