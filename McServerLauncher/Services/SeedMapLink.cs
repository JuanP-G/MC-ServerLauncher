using System.Globalization;

namespace McServerLauncher.Services;

/// <summary>
/// The link to a seed on Chunkbase's seed map, for the version the server runs.
/// </summary>
/// <remarks>
/// <para>
/// Chunkbase names its versions in its own way (<c>java_1_21_9</c> covers 1.21.9 to 1.21.11), so the
/// table below maps each Minecraft version to the newest entry at or below it. The entries were
/// copied from the site's own version picker.
/// </para>
/// <para>
/// A version the table does not know gets no <c>platform</c> at all, and Chunkbase then opens its
/// newest Java version. That is the right guess for a release newer than this table, and it beats
/// sending an id the site does not have. A snapshot gets the same treatment.
/// </para>
/// </remarks>
public static class SeedMapLink
{
    /// <summary>Chunkbase's Java versions, newest first: the first version each one covers, and its id.</summary>
    private static readonly (Version From, string Id)[] Platforms =
    {
        (new Version(26, 3), "java_26_3"),
        (new Version(26, 2), "java_26_2"),
        (new Version(26, 1), "java_26_1"),
        (new Version(1, 21, 9), "java_1_21_9"),
        (new Version(1, 21, 6), "java_1_21_6"),
        (new Version(1, 21, 5), "java_1_21_5"),
        (new Version(1, 21, 4), "java_1_21_4"),
        (new Version(1, 21, 2), "java_1_21_2"),
        (new Version(1, 21), "java_1_21"),
        (new Version(1, 20), "java_1_20"),
        (new Version(1, 19, 3), "java_1_19_3"),
        (new Version(1, 19), "java_1_19"),
        (new Version(1, 18), "java_1_18"),
        (new Version(1, 17), "java_1_17"),
        (new Version(1, 16), "java_1_16"),
        (new Version(1, 15), "java_1_15"),
        (new Version(1, 14), "java_1_14"),
        (new Version(1, 13), "java_1_13"),
        (new Version(1, 12), "java_1_12"),
        (new Version(1, 11), "java_1_11"),
        (new Version(1, 10), "java_1_10"),
        (new Version(1, 9), "java_1_9"),
        (new Version(1, 8), "java_1_8"),
        (new Version(1, 7), "java_1_7"),
    };

    /// <summary>The seed map for <paramref name="seed"/>, on the version closest to <paramref name="gameVersion"/>.</summary>
    public static string For(long seed, string? gameVersion)
    {
        var url = "https://www.chunkbase.com/apps/seed-map#seed=" + seed.ToString(CultureInfo.InvariantCulture);
        if (PlatformFor(gameVersion) is { } platform) url += "&platform=" + platform;
        return url + "&dimension=overworld";
    }

    /// <summary>Chunkbase's id for a Minecraft version, or null when it should be left to the site.</summary>
    internal static string? PlatformFor(string? gameVersion)
    {
        if (!Version.TryParse(gameVersion?.Trim(), out var version)) return null;   // snapshots, "", null

        // Newer than anything in the table: Chunkbase's own newest is a better guess than ours. By
        // major and minor only — 26.3.1 is still covered by 26.3, but 26.4 is not.
        var newest = Platforms[0].From;
        if ((version.Major, version.Minor).CompareTo((newest.Major, newest.Minor)) > 0) return null;

        foreach (var (from, id) in Platforms)
            if (Compare(version, from) >= 0) return id;

        return null;   // older than 1.7
    }

    // Version treats a missing build as -1, so "1.21" < "1.21.0"; compare as three plain numbers.
    private static int Compare(Version a, Version b)
    {
        var c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        return Math.Max(a.Build, 0).CompareTo(Math.Max(b.Build, 0));
    }
}
