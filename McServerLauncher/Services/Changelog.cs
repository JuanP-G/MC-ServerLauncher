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
        (new Version(1, 22, 0), "Whatsnew_1_22_0"),
        (new Version(1, 21, 6), "Whatsnew_1_21_6"),
        (new Version(1, 21, 5), "Whatsnew_1_21_5"),
        (new Version(1, 21, 4), "Whatsnew_1_21_4"),
        (new Version(1, 21, 3), "Whatsnew_1_21_3"),
        (new Version(1, 21, 2), "Whatsnew_1_21_2"),
        (new Version(1, 21, 1), "Whatsnew_1_21_1"),
        (new Version(1, 21, 0), "Whatsnew_1_21_0"),
        (new Version(1, 20, 0), "Whatsnew_1_20_0"),
        (new Version(1, 19, 2), "Whatsnew_1_19_2"),
        (new Version(1, 19, 1), "Whatsnew_1_19_1"),
        (new Version(1, 19, 0), "Whatsnew_1_19_0"),
        (new Version(1, 18, 0), "Whatsnew_1_18_0"),
        (new Version(1, 17, 0), "Whatsnew_1_17_0"),
        (new Version(1, 16, 0), "Whatsnew_1_16_0"),
        (new Version(1, 15, 0), "Whatsnew_1_15_0"),
        (new Version(1, 14, 0), "Whatsnew_1_14_0"),
        (new Version(1, 13, 1), "Whatsnew_1_13_1"),
        (new Version(1, 13, 0), "Whatsnew_1_13_0"),
        (new Version(1, 12, 3), "Whatsnew_1_12_3"),
        (new Version(1, 12, 2), "Whatsnew_1_12_2"),
        (new Version(1, 12, 1), "Whatsnew_1_12_1"),
        (new Version(1, 12, 0), "Whatsnew_1_12_0"),
        (new Version(1, 11, 2), "Whatsnew_1_11_2"),
        (new Version(1, 11, 1), "Whatsnew_1_11_1"),
        (new Version(1, 11, 0), "Whatsnew_1_11_0"),
        (new Version(1, 10, 3, 4), "Whatsnew_1_10_3_4"),
        (new Version(1, 10, 3, 3), "Whatsnew_1_10_3_3"),
        (new Version(1, 10, 3, 2), "Whatsnew_1_10_3_2"),
        (new Version(1, 10, 3, 1), "Whatsnew_1_10_3_1"),
        (new Version(1, 10, 3), "Whatsnew_1_10_3"),
        (new Version(1, 10, 2), "Whatsnew_1_10_2"),
        (new Version(1, 10, 1), "Whatsnew_1_10_1"),
        (new Version(1, 10, 0), "Whatsnew_1_10_0"),
        (new Version(1, 9, 2), "Whatsnew_1_9_2"),
        (new Version(1, 9, 1), "Whatsnew_1_9_1"),
        (new Version(1, 9, 0), "Whatsnew_1_9_0"),
        (new Version(1, 8, 2), "Whatsnew_1_8_2"),
        (new Version(1, 8, 1), "Whatsnew_1_8_1"),
        (new Version(1, 8, 0), "Whatsnew_1_8_0"),
        (new Version(1, 7, 3), "Whatsnew_1_7_3"),
        (new Version(1, 7, 2), "Whatsnew_1_7_2"),
        (new Version(1, 7, 1), "Whatsnew_1_7_1"),
        (new Version(1, 7, 0), "Whatsnew_1_7_0"),
        (new Version(1, 6, 3), "Whatsnew_1_6_3"),
        (new Version(1, 6, 2), "Whatsnew_1_6_2"),
        (new Version(1, 6, 1), "Whatsnew_1_6_1"),
        (new Version(1, 6, 0), "Whatsnew_1_6_0"),
        (new Version(1, 5, 0), "Whatsnew_1_5_0"),
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

    /// <summary>A release in the table, with its notes not yet looked up.</summary>
    /// <remarks>
    /// <see cref="Notes"/> asks the resources every time it is read. There are about fifty releases and
    /// a screen that lists them all only needs the text of the ones that are open.
    /// </remarks>
    public readonly record struct Release(Version Version, string Key)
    {
        /// <summary>The version as its release calls it: "1.22.0".</summary>
        public string Label => Format(Version);

        /// <summary>The notes in the language of the interface.</summary>
        public string Notes => Localizer.Get(Key);
    }

    /// <summary>
    /// The version this build is, as the table spells versions: three numbers, plus the fourth of a
    /// beta.
    /// </summary>
    internal static Version Running()
    {
        var v = typeof(Changelog).Assembly.GetName().Version ?? new Version(0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    /// <summary>A stored "last seen" version ("1.21.6"); null on a new install or an unreadable value.</summary>
    internal static Version? ParseSeen(string? seen)
    {
        if (string.IsNullOrWhiteSpace(seen) || !Version.TryParse(seen, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    /// <summary>
    /// Every release up to <paramref name="current"/>, newest first: the whole history this build
    /// knows, for the screen that lists it.
    /// </summary>
    public static IReadOnlyList<Release> All(Version current)
    {
        var result = new List<Release>();
        foreach (var (v, key) in Entries)
            if (v <= current) result.Add(new Release(v, key));
        return result;
    }

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

    /// <summary>
    /// The version as the release calls itself: three numbers, or four for a beta.
    /// </summary>
    /// <remarks>
    /// The fourth number is only shown when there is one. Printing "1.10.4.0" for a stable release
    /// would not match its own tag, its installer filename, or anything the user has seen.
    /// </remarks>
    internal static string Format(Version v) =>
        v.Revision > 0
            ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
            : $"{v.Major}.{v.Minor}.{v.Build}";
}
