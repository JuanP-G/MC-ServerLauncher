using System;
using System.Collections.Generic;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Per-version "what's new" notes. When the user updates across several versions at once, the notes
/// for every version they hadn't seen yet are shown together (newest first), so nothing is missed.
/// Add a new entry (newest first) plus its <c>Whatsnew_x_y_z</c> resx keys on each release.
/// </summary>
public static class Changelog
{
    // Newest first. Each version's notes live in a localized resx key.
    private static readonly (Version Version, string Key)[] Entries =
    {
        (new Version(1, 4, 0), "Whatsnew_1_4_0"),
        (new Version(1, 3, 0), "Whatsnew_1_3_0"),
        (new Version(1, 2, 5), "Whatsnew_1_2_5"),
        (new Version(1, 2, 4), "Whatsnew_1_2_4"),
        (new Version(1, 2, 3), "Whatsnew_1_2_3"),
        (new Version(1, 2, 2), "Whatsnew_1_2_2"),
        (new Version(1, 2, 1), "Whatsnew_1_2_1"),
        (new Version(1, 2, 0), "Whatsnew_1_2_0"),
        (new Version(1, 1, 0), "Whatsnew_1_1_0"),
    };

    /// <summary>A version and its localized notes.</summary>
    public readonly record struct Section(string Version, string Notes);

    /// <summary>
    /// Notes for every version newer than <paramref name="lastSeen"/> up to <paramref name="current"/>,
    /// newest first. On a fresh install (<paramref name="lastSeen"/> null) only the newest applicable
    /// version's notes are returned, so a first-time user isn't shown the whole history.
    /// </summary>
    public static IReadOnlyList<Section> NotesSince(Version? lastSeen, Version current)
    {
        var result = new List<Section>();
        foreach (var (v, key) in Entries)
        {
            if (v > current) continue; // released after this build

            if (lastSeen is null)
            {
                result.Add(new Section(Format(v), Localizer.Get(key)));
                break; // fresh install: only the newest applicable entry
            }

            if (v > lastSeen)
                result.Add(new Section(Format(v), Localizer.Get(key)));
        }
        return result;
    }

    private static string Format(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
