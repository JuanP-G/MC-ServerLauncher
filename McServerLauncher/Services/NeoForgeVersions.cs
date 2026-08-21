using System.Collections.Generic;
using System.Linq;

namespace McServerLauncher.Services;

/// <summary>
/// Turning "which Minecraft version?" into "which NeoForge build?".
/// </summary>
/// <remarks>
/// <para>
/// Forge answers this with a promotions feed that names a recommended build per Minecraft version.
/// NeoForge has no such thing: it publishes one flat list of every build ever, and the Minecraft
/// version is encoded <em>inside</em> the build number. So the only way to pick correctly is to know
/// how that encoding works.
/// </para>
/// <para>
/// Getting it wrong fails quietly and expensively — it would install a build for a different
/// Minecraft version, which downloads and installs perfectly and then refuses to start. That is why
/// this is a pure, separately tested pair of functions rather than a regex buried in the installer.
/// </para>
/// </remarks>
public static class NeoForgeVersions
{
    /// <summary>
    /// The prefix every NeoForge build for <paramref name="mcVersion"/> starts with, or null when
    /// the Minecraft version isn't in a shape NeoForge uses.
    /// </summary>
    /// <remarks>
    /// Two schemes exist, because Minecraft itself changed how it numbers releases:
    /// <list type="bullet">
    /// <item>Classic <c>1.MINOR[.PATCH]</c> maps to <c>MINOR.PATCH.</c> — 1.21.1 → "21.1.",
    /// and 1.21 → "21.0.", because a missing patch is zero, not absent.</item>
    /// <item>The newer <c>YY.N[.PATCH]</c> maps to <c>YY.N.PATCH.</c> — 26.2 → "26.2.0.",
    /// 26.1.2 → "26.1.2.". Same rule, one component further along.</item>
    /// </list>
    /// The trailing dot is deliberate: without it "21.1" would also match every "21.10.x" and
    /// "21.11.x" build, quietly offering a Minecraft 1.21.10 loader to a 1.21.1 server.
    /// </remarks>
    public static string? PrefixFor(string? mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return null;

        var parts = mcVersion.Trim().Split('.');
        if (parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;

        // Classic "1.x[.y]": drop the leading 1, then pad the patch.
        if (parts[0] == "1")
        {
            if (parts.Length is < 2 or > 3) return null;
            var patch = parts.Length == 3 ? parts[2] : "0";
            return $"{parts[1]}.{patch}.";
        }

        // Newer "YY.N[.p]": the whole thing is the prefix, padded to three components.
        if (parts.Length is < 2 or > 3) return null;
        var third = parts.Length == 3 ? parts[2] : "0";
        return $"{parts[0]}.{parts[1]}.{third}.";
    }

    /// <summary>
    /// The Minecraft version a NeoForge build belongs to — the inverse of <see cref="PrefixFor"/>,
    /// used when reading an already-installed server off disk, where the build number is all there
    /// is to go on. Null if the build number isn't in a shape NeoForge uses.
    /// </summary>
    public static string? MinecraftVersionOf(string? neoForgeVersion)
    {
        if (string.IsNullOrWhiteSpace(neoForgeVersion)) return null;

        var core = neoForgeVersion.Trim().Split('-')[0];      // drop a "-beta" suffix
        var parts = core.Split('.');
        if (parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;

        // Classic: MINOR.PATCH.BUILD -> 1.MINOR[.PATCH]
        if (parts.Length == 3)
            return parts[1] == "0" ? $"1.{parts[0]}" : $"1.{parts[0]}.{parts[1]}";

        // Newer: YY.N.PATCH.BUILD -> YY.N[.PATCH]
        if (parts.Length == 4)
            return parts[2] == "0" ? $"{parts[0]}.{parts[1]}" : $"{parts[0]}.{parts[1]}.{parts[2]}";

        return null;
    }

    /// <summary>A chosen build, and whether the user is about to install a pre-release.</summary>
    public record Choice(string Version, bool IsBeta);

    /// <summary>
    /// Picks the build to install for <paramref name="mcVersion"/> out of the full published list,
    /// or null when NeoForge has nothing for that Minecraft version at all.
    /// </summary>
    /// <remarks>
    /// Stable wins whenever one exists. Six Minecraft versions (1.20.3, 1.20.5, 1.21.2, 1.21.6,
    /// 1.21.7, 1.21.9) have only ever had betas, and refusing those would mean telling people
    /// NeoForge is unavailable when in practice it is what everyone runs — so a beta is offered,
    /// and <see cref="Choice.IsBeta"/> exists so the app can say so before installing rather than
    /// after.
    /// </remarks>
    public static Choice? Pick(IEnumerable<string> allVersions, string? mcVersion)
    {
        var prefix = PrefixFor(mcVersion);
        if (prefix is null) return null;

        var family = allVersions
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (family.Count == 0) return null;

        var stable = family.Where(v => !IsPreRelease(v)).ToList();
        var pool = stable.Count > 0 ? stable : family;

        var best = pool.OrderBy(v => v, NeoForgeOrder.Instance).Last();
        return new Choice(best, IsPreRelease(best));
    }

    /// <summary>NeoForge marks pre-releases with a suffix, e.g. "21.7.25-beta".</summary>
    private static bool IsPreRelease(string version) => version.Contains('-');

    /// <summary>
    /// Orders builds by their numbers rather than as text.
    /// </summary>
    /// <remarks>
    /// Plain string ordering puts "21.1.9" above "21.1.248", which would pin every server to an
    /// ancient build. The published list happens to arrive in order, but relying on that would make
    /// correctness depend on someone else's undocumented behaviour.
    /// </remarks>
    private sealed class NeoForgeOrder : IComparer<string>
    {
        public static readonly NeoForgeOrder Instance = new();

        public int Compare(string? a, string? b)
        {
            var x = Numbers(a);
            var y = Numbers(b);

            for (var i = 0; i < Math.Max(x.Count, y.Count); i++)
            {
                var left = i < x.Count ? x[i] : 0;
                var right = i < y.Count ? y[i] : 0;
                if (left != right) return left.CompareTo(right);
            }

            // Same numbers: a stable build sorts above its own beta.
            return string.CompareOrdinal(a, b);
        }

        private static List<int> Numbers(string? version)
        {
            var core = version?.Split('-')[0] ?? string.Empty;
            return core.Split('.')
                .Select(p => int.TryParse(p, out var n) ? n : 0)
                .ToList();
        }
    }
}
