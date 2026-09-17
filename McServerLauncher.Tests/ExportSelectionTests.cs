using McServerLauncher.Services;
using Side = McServerLauncher.Services.ContentManifest.ContentSide;
using Store = McServerLauncher.Services.ExportSelection.StoreSide;

namespace McServerLauncher.Tests;

/// <summary>
/// The table that decides which jars a player's pack gets.
/// </summary>
/// <remarks>
/// <para>
/// Getting this wrong has two very different costs. Including a server-only mod wastes a few
/// megabytes and some memory. Excluding one the player needed means Minecraft dies on startup with
/// a missing-dependency screen, on their machine, with nothing linking it back to a decision this
/// app made silently — so the table is deliberately asymmetric, and every rule below is one of the
/// ways it refuses to be clever.
/// </para>
/// <para>
/// One test per row, named after what the row is protecting.
/// </para>
/// </remarks>
public class ExportSelectionTests
{
    /// <summary>A jar that declares a side, and optionally what it provides and needs.</summary>
    private static ExportSelection.Candidate Jar(
        string fileName,
        Side side = Side.Unspecified,
        Store client = Store.Unknown,
        Store server = Store.Unknown,
        string[]? provides = null,
        string[]? requires = null) =>
        new(new ContentManifest.Manifest(
                fileName, provides ?? Array.Empty<string>(), requires ?? Array.Empty<string>())
            { Side = side },
            client, server);

    private static ExportSelection.Plan Decide(params ExportSelection.Candidate[] jars) =>
        ExportSelection.Decide(jars);

    // --- The rows of the table ---

    [Fact]
    public void BothSourcesSayServerOnly()
    {
        var plan = Decide(
            Jar("geyser.jar", Side.Server, client: Store.Unsupported),
            Jar("sodium.jar", Side.Client));

        Assert.Equal(new[] { "geyser.jar" }, plan.Excluded);
        Assert.Equal(new[] { "sodium.jar" }, plan.Included);
    }

    [Fact]
    public void TheJarSaysServerAndNothingContradictsIt()
    {
        // Offline, or a project the store has never heard of. The author's own file said it, and
        // there is nothing on the other side of the scale.
        var plan = Decide(
            Jar("backups.jar", Side.Server),
            Jar("sodium.jar", Side.Client));

        Assert.Equal(new[] { "backups.jar" }, plan.Excluded);
    }

    [Fact]
    public void TheJarSaysServerButTheStoreSaysTheClientNeedsIt()
    {
        // Two assertions that disagree. The jar is kept: a wrong inclusion costs megabytes, a wrong
        // exclusion costs the player a server that will not start.
        foreach (var client in new[] { Store.Required, Store.Optional })
        {
            var plan = Decide(Jar("raro.jar", Side.Server, client: client), Jar("otro.jar"));
            Assert.Empty(plan.Excluded);
        }
    }

    [Fact]
    public void TheJarSaysNothingAndTheStoreSaysTheClientCannotUseIt()
    {
        // The common case, and the one the store is actually needed for. Most authors never fill
        // the field in, so a jar's silence is the format's default rather than a statement — which
        // leaves the store as the only source that has said anything at all.
        var plan = Decide(
            Jar("floodgate.jar", client: Store.Unsupported, server: Store.Required),
            Jar("sodium.jar"));

        Assert.Equal(new[] { "floodgate.jar" }, plan.Excluded);
    }

    [Fact]
    public void TheJarSaysBothOutrightAndTheStoreDisagrees()
    {
        // Kept. Modrinth's side fields are community-edited and sometimes simply wrong, and an
        // author who wrote environment:"*" in their own manifest was asked the question and
        // answered it. A page anyone can edit does not overrule that.
        var plan = Decide(Jar("discutido.jar", Side.Both, client: Store.Unsupported), Jar("otro.jar"));

        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void AClientModIsNeverLeftOutWhateverTheStoreSays()
    {
        var plan = Decide(Jar("sodium.jar", Side.Client, client: Store.Unsupported), Jar("otro.jar"));

        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void TwoSilencesNeverExcludeAnything()
    {
        // The rule in one line: excluding takes an assertion. Nothing said by anybody is not one.
        var plan = Decide(Jar("uno.jar"), Jar("dos.jar"), Jar("tres.jar"));

        Assert.Empty(plan.Excluded);
        Assert.Equal(3, plan.Included.Count);
    }

    // --- The three rules above the table ---

    [Fact]
    public void MetadataThatSaysNeitherSideIsIgnored()
    {
        // O1. A project unsupported on both sides runs nowhere, which no published mod does — it is
        // a page nobody filled in. Read literally it would drop a jar on the strength of metadata
        // that is self-evidently broken, and this happens on Modrinth often enough to matter.
        var plan = Decide(
            Jar("mal-rellenado.jar", client: Store.Unsupported, server: Store.Unsupported),
            Jar("otro.jar"));

        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void ALibraryAKeptModNeedsIsNeverLeftOut()
    {
        // O2, and the crash this whole feature must not cause. The library says server-side — its
        // author was thinking about where it runs on the server — and a client mod in the same
        // folder depends on it. Dropped, the player's Minecraft dies on startup.
        var plan = Decide(
            Jar("libreria.jar", Side.Server, client: Store.Unsupported, provides: new[] { "libreria" }),
            Jar("mimod.jar", Side.Client, requires: new[] { "libreria" }));

        Assert.Empty(plan.Excluded);
        Assert.Contains("libreria.jar", plan.Included);
    }

    [Fact]
    public void TheDependencyRescueIsTransitive()
    {
        // A rescued library has dependencies of its own. Rescuing one level and stopping would
        // trade a missing-dependency crash for a different missing-dependency crash.
        var plan = Decide(
            Jar("base.jar", Side.Server, provides: new[] { "base" }),
            Jar("media.jar", Side.Server, provides: new[] { "media" }, requires: new[] { "base" }),
            Jar("mimod.jar", Side.Client, requires: new[] { "media" }));

        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void ALibraryNothingKeptNeedsStaysOut()
    {
        // The other half of O2: the rescue follows real dependencies, it does not keep every
        // library on principle. Here the only jar needing it is itself server-only.
        var plan = Decide(
            Jar("libreria.jar", Side.Server, provides: new[] { "libreria" }),
            Jar("geyser.jar", Side.Server, requires: new[] { "libreria" }),
            Jar("sodium.jar", Side.Client));

        Assert.Equal(new[] { "libreria.jar", "geyser.jar" }, plan.Excluded);
    }

    [Fact]
    public void AnExportIsNeverEmptied()
    {
        // O3, the floor. If the reasoning wants to throw everything away, the reasoning is what is
        // wrong — a pack with no mods in it is never the answer the user was asking for.
        var plan = Decide(
            Jar("geyser.jar", Side.Server),
            Jar("floodgate.jar", Side.Server));

        Assert.Empty(plan.Excluded);
        Assert.Equal(2, plan.Included.Count);
    }

    // --- The escape hatches ---

    [Fact]
    public void IncludingThemAnywayDoesExactlyThat()
    {
        var jars = new[]
        {
            Jar("geyser.jar", Side.Server, client: Store.Unsupported),
            Jar("sodium.jar", Side.Client)
        };

        var plan = ExportSelection.Decide(jars, includeEverything: true);

        Assert.Empty(plan.Excluded);
        Assert.Equal(new[] { "geyser.jar", "sodium.jar" }, plan.Included);
        Assert.False(plan.LeftSomethingOut);
    }

    [Fact]
    public void OfflineNothingNewIsLeftOut()
    {
        // The store not answering must only ever exclude *fewer* jars than answering would. The
        // pack differs with and without a connection, and the notice says so — but it differs in
        // the direction where being wrong is cheap.
        var jars = new[]
        {
            Jar("floodgate.jar", client: Store.Unsupported),   // only the store knows
            Jar("geyser.jar", Side.Server, client: Store.Unsupported),
            Jar("sodium.jar", Side.Client)
        };

        var online = ExportSelection.Decide(jars);
        var offline = ExportSelection.Decide(jars
            .Select(j => j with { ClientSide = Store.Unknown, ServerSide = Store.Unknown })
            .ToList());

        Assert.Equal(new[] { "floodgate.jar", "geyser.jar" }, online.Excluded);
        Assert.Equal(new[] { "geyser.jar" }, offline.Excluded);
        Assert.Subset(online.Excluded.ToHashSet(), offline.Excluded.ToHashSet());
    }

    [Fact]
    public void NothingInTheFolderIsNotAFailure()
    {
        var plan = ExportSelection.Decide(Array.Empty<ExportSelection.Candidate>());

        Assert.Empty(plan.Included);
        Assert.Empty(plan.Excluded);
        Assert.False(plan.LeftSomethingOut);
    }

    // --- The promise the table rests on ---

    [Fact]
    public void TheDecisionNeverTouchesTheNetwork()
    {
        // Same shape as StartDependencyGateTests: the promise is checked against the file, not
        // against a comment. Exporting has a four-second budget for the store and degrades to
        // jar-only when it runs out; a lookup sneaking in here would turn a degraded answer into a
        // hang, on the click where the user is watching a progress bar.
        var source = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Services", "ExportSelection.cs"));

        foreach (var forbidden in new[] { "HttpClient", "ModrinthService", "System.Net" })
            Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                $"ExportSelection menciona «{forbidden}»: la tabla decide sin red");
    }
}
