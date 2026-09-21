namespace McServerLauncher.ViewModels;

/// <summary>
/// Whether an installed jar matches what was typed in the installed list's search box.
/// </summary>
/// <remarks>
/// <para>
/// Every word typed has to appear somewhere in the file name, in any order and in any case. Hyphens,
/// underscores and dots count as spaces on both sides, because that is how jars are named and not
/// how people type: "sodium extra" has to find <c>sodium-extra-0.5.4+mc1.21.jar</c>.
/// </para>
/// <para>
/// Only the file name, never the jar's contents. The name carries the mod's name almost always,
/// and opening every jar on every keystroke to read its manifest would cost far more than the
/// handful of cases it would catch.
/// </para>
/// </remarks>
public static class InstalledModFilter
{
    private static readonly char[] Separators = { ' ', '-', '_', '.', '+' };

    /// <summary>The words of a search, lower-cased; none at all for an empty box.</summary>
    public static string[] Words(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? Array.Empty<string>()
            : search.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    public static bool Matches(string fileName, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return true;

        // Each word on its own, which is what makes the order not matter; a word may be part of a
        // longer one, so "sod" finds sodium.
        var name = fileName.ToLowerInvariant();
        foreach (var word in words)
            if (!name.Contains(word, StringComparison.Ordinal)) return false;
        return true;
    }
}
