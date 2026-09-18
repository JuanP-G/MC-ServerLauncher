using System;
using System.Collections.Generic;
using System.Linq;

namespace McServerLauncher.Services;

/// <summary>
/// Which of a server's jars belong in the pack its players are handed.
/// </summary>
/// <remarks>
/// <para>
/// A modded server usually carries a few jars the player has no use for: Geyser and Floodgate for
/// Bedrock crossplay, a permissions plugin, a world-backup mod. They do nothing on a client, and
/// they are megabytes the player downloads and copies into their mods folder for no reason.
/// </para>
/// <para>
/// Two sources answer the question, and neither on its own is enough. The jar's own manifest is the
/// author speaking, but hardly any author ever changes the field. The store knows, but its data is
/// community-edited and is sometimes plainly wrong. So the rule is asymmetric, and it is one
/// sentence: <b>a jar leaves the pack when either source says it is server-only and neither says a
/// client needs it.</b> A jar that declares itself client-side can never be dropped, whatever the
/// store says.
/// </para>
/// <para>
/// "Declares itself both" is <em>not</em> in that list, and finding out why cost a real modpack. The
/// first folder this was tried on had eleven mods and all eleven said <c>environment: "*"</c> —
/// including Floodgate, which is a Bedrock authentication plugin and does nothing whatsoever on a
/// client. It is what the Fabric template writes and it means the same as leaving the key out, so
/// treating it as an assertion strong enough to overrule the store made the whole feature incapable
/// of excluding anything at all. Only <c>client</c> and <c>server</c> are worth anything in that
/// field.
/// </para>
/// <para>
/// Nothing here touches the network — see <c>ExportGateTests</c>, which checks that against the
/// file. What the store said is passed in, already fetched, already given up on if it did not
/// arrive. Offline the store simply says nothing, and saying nothing can only ever exclude
/// <em>fewer</em> jars, which is the direction a wrong answer should fail in.
/// </para>
/// </remarks>
public static class ExportSelection
{
    /// <summary>How well a project supports one side, in the store's vocabulary.</summary>
    public enum StoreSide
    {
        /// <summary>Not asked, not answered, or not reachable. Carries no weight either way.</summary>
        Unknown,

        /// <summary>The side needs it.</summary>
        Required,

        /// <summary>The side can use it.</summary>
        Optional,

        /// <summary>The side cannot use it at all.</summary>
        Unsupported
    }

    /// <summary>One jar up for inclusion: what it says about itself, and what the store says.</summary>
    /// <param name="Manifest">Read from the jar. The author speaking, when the author bothered.</param>
    /// <param name="ClientSide">The store's <c>client_side</c>, or Unknown when it was not had.</param>
    /// <param name="ServerSide">The store's <c>server_side</c>. Only used to spot broken metadata.</param>
    public sealed record Candidate(
        ContentManifest.Manifest Manifest,
        StoreSide ClientSide = StoreSide.Unknown,
        StoreSide ServerSide = StoreSide.Unknown);

    /// <summary>What an export decided, by jar file name.</summary>
    /// <param name="Included">Goes into the pack.</param>
    /// <param name="Excluded">Left out as server-only, in the order the jars were given.</param>
    public sealed record Plan(IReadOnlyList<string> Included, IReadOnlyList<string> Excluded)
    {
        /// <summary>True when something was left out and the user is owed an explanation.</summary>
        public bool LeftSomethingOut => Excluded.Count > 0;
    }

    /// <summary>Decides what goes in the pack.</summary>
    /// <param name="candidates">Every enabled jar in the server's content folder.</param>
    /// <param name="includeEverything">
    /// The user pressed "include them anyway". Nothing is excluded and no reasoning is done.
    /// </param>
    public static Plan Decide(IReadOnlyList<Candidate> candidates, bool includeEverything = false)
    {
        var names = candidates.Select(c => c.Manifest.FileName).ToList();
        if (includeEverything || candidates.Count == 0)
            return new Plan(names, Array.Empty<string>());

        var excluded = candidates.Where(IsServerOnly).Select(c => c.Manifest.FileName).ToHashSet();

        RescueWhatTheKeptOnesNeed(candidates, excluded);

        // O3, the floor. An empty modpack is never the right answer: if the reasoning above wants
        // to throw everything away, the reasoning is what is wrong, not the server.
        if (excluded.Count == candidates.Count) excluded.Clear();

        return new Plan(
            names.Where(n => !excluded.Contains(n)).ToList(),
            names.Where(excluded.Contains).ToList());
    }

    /// <summary>
    /// The table: one assertion, no contradiction.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Jar says Server, store agrees or says nothing → out.</item>
    /// <item>Jar says Server, store says the client needs or can use it → kept. They disagree, and
    /// the disagreement is resolved in the direction where being wrong is cheap.</item>
    /// <item>Jar says nothing or "both", store says the client cannot use it → out. Neither of
    /// those is an author's claim: both formats mean "everywhere" when nothing is said, and both
    /// templates write the "everywhere" value, so the store is the only one talking.</item>
    /// <item>Jar says Client → kept, always. Nothing here can ever drop a client mod.</item>
    /// </list>
    /// </remarks>
    private static bool IsServerOnly(Candidate candidate)
    {
        var (client, _) = Believable(candidate);

        return candidate.Manifest.Side switch
        {
            // The only jar-side claim strong enough to stand alone.
            ContentManifest.ContentSide.Server =>
                client is not (StoreSide.Required or StoreSide.Optional),

            // The only one strong enough to overrule the store.
            ContentManifest.ContentSide.Client => false,

            // Unspecified and Both are the same thing wearing different hats: the format's default,
            // written out or left out. Neither blocks the store from answering.
            _ => client == StoreSide.Unsupported
        };
    }

    /// <summary>
    /// The store's answer, with the version of it that means nothing thrown away.
    /// </summary>
    /// <remarks>
    /// O1. A project marked unsupported on <em>both</em> sides cannot run anywhere, which no
    /// published mod is — it is a page nobody filled in properly. This happens on Modrinth often
    /// enough to matter, and read literally it would exclude a jar on the strength of metadata that
    /// is self-evidently broken.
    /// </remarks>
    private static (StoreSide Client, StoreSide Server) Believable(Candidate candidate) =>
        candidate.ClientSide == StoreSide.Unsupported && candidate.ServerSide == StoreSide.Unsupported
            ? (StoreSide.Unknown, StoreSide.Unknown)
            : (candidate.ClientSide, candidate.ServerSide);

    /// <summary>
    /// Puts back anything a kept jar needs, following the chain until it stops moving.
    /// </summary>
    /// <remarks>
    /// O2, and the most important rule here. A library marked server-side that a client mod depends
    /// on is exactly the crash this feature must not cause — the player installs the pack, Minecraft
    /// dies on startup with a missing-dependency screen, and nothing connects that to a checkbox
    /// they never saw. Transitive because a rescued library has dependencies of its own, and
    /// rescuing it while dropping those would trade one crash for another.
    /// </remarks>
    private static void RescueWhatTheKeptOnesNeed(
        IReadOnlyList<Candidate> candidates, HashSet<string> excluded)
    {
        // Loops at most once per jar: every pass either rescues one or stops.
        for (var pass = 0; pass <= candidates.Count; pass++)
        {
            var needed = candidates
                .Where(c => !excluded.Contains(c.Manifest.FileName))
                .SelectMany(c => c.Manifest.Requires)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rescued = candidates
                .Where(c => excluded.Contains(c.Manifest.FileName))
                .Where(c => c.Manifest.Provides.Any(needed.Contains))
                .Select(c => c.Manifest.FileName)
                .ToList();

            if (rescued.Count == 0) return;
            foreach (var name in rescued) excluded.Remove(name);
        }
    }
}
