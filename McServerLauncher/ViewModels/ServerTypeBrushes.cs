using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The per-type badge colours, as brushes.
/// </summary>
/// <remarks>
/// The colours themselves now live in <see cref="ServerTypeCatalog"/> alongside everything else
/// known about a type; this only turns them into Avalonia brushes and caches them, so the table
/// stays free of any UI framework and cannot drift from the picker's colours.
/// </remarks>
public static class ServerTypeBrushes
{
    private static readonly Dictionary<ServerType, IBrush> Cache = Build();
    private static readonly IBrush Unknown = new ImmutableSolidColorBrush(Color.Parse("#6E7681"));

    /// <summary>Badge colour for a server type; unknown/future types fall back to grey.</summary>
    public static IBrush For(ServerType type) =>
        Cache.TryGetValue(type, out var brush) ? brush : Unknown;

    private static Dictionary<ServerType, IBrush> Build()
    {
        var map = new Dictionary<ServerType, IBrush>();
        foreach (var entry in ServerTypeCatalog.All)
            map[entry.Type] = new ImmutableSolidColorBrush(Color.Parse(entry.BadgeColor));
        return map;
    }
}
