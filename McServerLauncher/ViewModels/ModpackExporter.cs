using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>What one export put into the pack, and what it left out.</summary>
/// <param name="Included">File names of the jars written, in the order they were written.</param>
/// <param name="Excluded">File names left out as server-only.</param>
/// <param name="AskedTheStore">False when the pack was built on the jars alone.</param>
public sealed record ModpackResult(
    IReadOnlyList<string> Included, IReadOnlyList<string> Excluded, bool AskedTheStore);

/// <summary>
/// Builds the zip a server's owner hands to their players: the mods the players' game needs, the
/// instructions for installing them, and the scripts that do it.
/// </summary>
/// <remarks>
/// <para>
/// It takes a path rather than opening a picker, which is the point: the decision about what goes in
/// is the part worth testing, and it was unreachable without a main window while it lived in the
/// Mods tab's view model. The tab keeps the file picker and the notice that follows; this class
/// keeps everything between "here is where to write it" and "here is what it contained".
/// </para>
/// <para>
/// Which jars a player needs is decided by <see cref="ExportSelection"/> from what each jar declares
/// about itself and, when it can be asked in time, what the store says about each side of the game.
/// </para>
/// </remarks>
public sealed class ModpackExporter
{
    /// <summary>How long the store gets to answer before the pack is built without it.</summary>
    /// <remarks>
    /// A few seconds, because what the store adds is an improvement and not a requirement: without
    /// it the table falls back to what the jars themselves declare, which excludes strictly less.
    /// Nobody should watch a progress bar because Modrinth is having a slow afternoon.
    /// </remarks>
    private static readonly TimeSpan StoreLookupBudget = TimeSpan.FromSeconds(4);

    private static readonly IReadOnlyDictionary<string,
        (ExportSelection.StoreSide Client, ExportSelection.StoreSide Server)>
        EmptySides = new Dictionary<string, (ExportSelection.StoreSide, ExportSelection.StoreSide)>();

    private readonly ServerConfig _config;
    private readonly FileHashCache _hashes;
    private readonly ModrinthService _store;
    private readonly Func<IEnumerable<string>, CancellationToken, Task> _learn;
    private readonly Func<string, string?> _projectIdOf;
    private readonly Func<string> _howToPlay;

    /// <param name="config">The server whose content is packed.</param>
    /// <param name="hashes">Shared with the update scan: hashing a jar is the expensive part.</param>
    /// <param name="store">Asked for each project's client and server support.</param>
    /// <param name="learn">Resolves jar hashes to store versions (so <paramref name="projectIdOf"/> can answer).</param>
    /// <param name="projectIdOf">The store project a jar hash is known to belong to, if any.</param>
    /// <param name="howToPlay">The instructions for joining, depending on the server's type.</param>
    public ModpackExporter(ServerConfig config, FileHashCache hashes, ModrinthService store,
        Func<IEnumerable<string>, CancellationToken, Task> learn, Func<string, string?> projectIdOf,
        Func<string> howToPlay)
    {
        _config = config;
        _hashes = hashes;
        _store = store;
        _learn = learn;
        _projectIdOf = projectIdOf;
        _howToPlay = howToPlay;
    }

    private bool IsPluginBased => ServerTypeCatalog.IsPluginBased(_config.Type);
    private string ContentFolder => ServerTypeCatalog.ContentFolder(_config.Type);

    /// <summary>
    /// Builds the pack at <paramref name="destination"/>, replacing whatever was there.
    /// </summary>
    /// <param name="destination">Where to write the zip.</param>
    /// <param name="includeEverything">Skip the reasoning and pack the folder as it stands.</param>
    /// <param name="ct">Cancels the build.</param>
    public async Task<ModpackResult> BuildAsync(string destination, bool includeEverything, CancellationToken ct)
    {
        // Plugins never go anywhere near a player's machine, so there is nothing to work out: the
        // pack is the folder. The button is hidden on those servers, and this is the same answer
        // arrived at without relying on that.
        var everything = includeEverything || IsPluginBased;
        var sides = everything ? EmptySides : await StoreSidesAsync(JarsInContentFolder(), ct);

        // Worked out here so the script the player runs has no version to resolve and no choice to
        // make. Null on any failure — offline, a slow maven, a loader with no published checksum —
        // and the pack then only tells them what to install, which is worse but not broken.
        var loader = IsPluginBased
            ? null
            : await ClientLoaderInstall.ResolveAsync(
                _config.Type, _config.GameVersion, _config.ModLoaderVersion, ct);

        return await BuildWithSidesAsync(destination, sides, includeEverything, loader, ct);
    }

    /// <summary>
    /// The half that decides and writes, with whatever the store had to say already in hand.
    /// </summary>
    /// <remarks>
    /// Split from the fetching so the decision can be tested against real jars on disk and a real
    /// zip coming out, without a test ever reaching api.modrinth.com — which would make the result
    /// depend on a third party's uptime and on data anyone can edit.
    /// </remarks>
    /// <param name="destination">Where to write the zip.</param>
    /// <param name="storeSides">Per file name, what the store said. Empty means it was not asked.</param>
    /// <param name="includeEverything">Skip the reasoning and pack the folder as it stands.</param>
    /// <param name="loader">The client installer to offer, or null to only warn about it.</param>
    /// <param name="ct">Cancels the build.</param>
    internal Task<ModpackResult> BuildWithSidesAsync(
        string destination,
        IReadOnlyDictionary<string, (ExportSelection.StoreSide Client, ExportSelection.StoreSide Server)> storeSides,
        bool includeEverything,
        ClientLoaderInstall.Plan? loader,
        CancellationToken ct)
    {
        var jars = JarsInContentFolder();
        var everything = includeEverything || IsPluginBased;

        return Task.Run(() =>
        {
            var plan = Decide(jars, storeSides, everything);
            var keep = plan.Included.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var texts = BuildTexts(plan, loader);

            // The client-only mods go in as they are: they are in the pack because somebody put
            // them there for the players, so neither the table nor the server's folder gets a say.
            // A name the server's jars already use is skipped: two files in one folder cannot share
            // it, and the server's copy is the one the players need to match.
            var included = jars.Where(j => keep.Contains(Path.GetFileName(j))).ToList();
            var packJars = IsPluginBased
                ? new List<string>()
                : ClientPack.Jars(_config)
                    .Where(j => !included.Any(i => string.Equals(Path.GetFileName(i),
                        Path.GetFileName(j), StringComparison.OrdinalIgnoreCase)))
                    .ToList();

            ModpackWriter.Write(destination, ContentFolder, included.Concat(packJars).ToList(), texts);

            // "Not degraded" rather than "a request went out": packing everything needs no store
            // answer, so it is not the reduced result the offline note is there to explain.
            var packed = plan.Included.Concat(packJars.Select(j => Path.GetFileName(j))).ToList();
            return new ModpackResult(packed, plan.Excluded, everything || storeSides.Count > 0);
        }, ct);
    }

    /// <summary>Every enabled jar in this server's content folder, in a stable order.</summary>
    private List<string> JarsInContentFolder()
    {
        var modsFolder = Path.Combine(_config.FolderPath, ContentFolder);

        return Directory.Exists(modsFolder)
            ? Directory.EnumerateFiles(modsFolder, "*.jar")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
    }

    /// <summary>Which jars the players need: what each declares, corrected by what the store says.</summary>
    private static ExportSelection.Plan Decide(
        IReadOnlyList<string> jars,
        IReadOnlyDictionary<string, (ExportSelection.StoreSide Client, ExportSelection.StoreSide Server)> sides,
        bool everything)
    {
        var candidates = jars
            .Select(path =>
            {
                var manifest = ContentManifest.Read(path);
                return sides.TryGetValue(manifest.FileName, out var side)
                    ? new ExportSelection.Candidate(manifest, side.Client, side.Server)
                    : new ExportSelection.Candidate(manifest);
            })
            .ToList();

        return ExportSelection.Decide(candidates, everything);
    }

    /// <summary>The instructions and install scripts that travel in the pack.</summary>
    private List<ModpackWriter.TextFile> BuildTexts(ExportSelection.Plan plan, ClientLoaderInstall.Plan? loader)
    {
        var instructions = string.Format(Localizer.Get("Export_InstructionsFmt"),
            _config.Name, _config.Type, _config.GameVersion, _howToPlay(), ContentFolder);

        // The pack has to explain itself to whoever receives it, not only to whoever made it:
        // a player who counts the jars and finds three missing deserves to read why here.
        if (plan.Excluded.Count == 1)
            instructions += "\n\n" + string.Format(
                Localizer.Get("Export_LeftOutOneFmt"), plan.Excluded[0]);
        else if (plan.LeftSomethingOut)
            instructions += "\n\n" + string.Format(Localizer.Get("Export_LeftOutFmt"),
                plan.Excluded.Count, string.Join(", ", plan.Excluded));

        // The script names are not translated — see InstallScriptBuilder — so the
        // instructions, which are, have to be where the player reads what they are for.
        if (!IsPluginBased)
            instructions += "\n\n" + string.Format(Localizer.Get("Export_ScriptsFmt"),
                InstallScriptBuilder.WindowsName, InstallScriptBuilder.UnixName);

        var texts = new List<ModpackWriter.TextFile>
        {
            // CRLF whatever built the pack: this one is opened in Notepad more often than
            // anywhere else, and every other editor reads CRLF without complaining.
            new(Localizer.Get("Export_InstructionsFile"), instructions, Newline: "\r\n")
        };

        // Plugins go on a server, by hand, by whoever runs it. Nothing to install on a
        // client, so no script — and the export button is not offered on those servers.
        if (!IsPluginBased)
            texts.AddRange(InstallScriptBuilder.Build(
                _config.Name, _config.Type, _config.GameVersion, DateTime.Now, loader));

        return texts;
    }

    /// <summary>
    /// What the store says about each jar's sides, by file name. Empty when it could not be asked.
    /// </summary>
    /// <remarks>
    /// Two hops, because a hash lookup alone does not carry the side fields: SHA-1 to project id
    /// (memoised, and usually already known from the update check), then the project itself, which
    /// the store service caches for six hours with a copy on disk.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, (ExportSelection.StoreSide Client, ExportSelection.StoreSide Server)>>
        StoreSidesAsync(IReadOnlyList<string> jarPaths, CancellationToken ct)
    {
        var byName = new Dictionary<string, (ExportSelection.StoreSide, ExportSelection.StoreSide)>(
            StringComparer.OrdinalIgnoreCase);
        if (jarPaths.Count == 0) return byName;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(StoreLookupBudget);

        try
        {
            var hashByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in jarPaths)
            {
                try { hashByName[Path.GetFileName(path)] = await _hashes.Sha1Async(path, budget.Token); }
                catch { /* unreadable or locked: that jar simply gets no store answer */ }
            }

            await _learn(hashByName.Values, budget.Token);

            var ids = hashByName.Values
                .Select(_projectIdOf)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (ids.Count == 0) return byName;

            var projects = await _store.GetProjectsAsync(ids, budget.Token);
            if (projects is null) return byName;

            // Grouped rather than ToDictionary: a duplicate id in the response would throw, and
            // taking the store down with an export is not a trade worth making.
            var byId = projects
                .Where(project => !string.IsNullOrEmpty(project.Id))
                .GroupBy(project => project.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var (name, hash) in hashByName)
                if (_projectIdOf(hash) is { } id && byId.TryGetValue(id, out var project))
                    byName[name] = (StoreSideOf(project.ClientSide), StoreSideOf(project.ServerSide));
        }
        catch
        {
            // Offline, out of time, or Modrinth having a bad day. The table falls back to the jars
            // alone, which excludes fewer of them, and the notice says the pack was built that way.
        }

        return byName;
    }

    /// <summary>Modrinth's word for how well a side is supported, as the table understands it.</summary>
    private static ExportSelection.StoreSide StoreSideOf(string? value) => value?.ToLowerInvariant() switch
    {
        "required" => ExportSelection.StoreSide.Required,
        "optional" => ExportSelection.StoreSide.Optional,
        "unsupported" => ExportSelection.StoreSide.Unsupported,
        _ => ExportSelection.StoreSide.Unknown
    };

    /// <summary>The sentence the notice shows for one finished export.</summary>
    public static string NoticeFor(ModpackResult result)
    {
        // One excluded file is the common case — a lone Geyser or Floodgate — and "1 archivos" is
        // the sort of thing that makes an app look machine-written. Each language gets to say it
        // its own way instead of the count being pasted into a plural sentence.
        var text = result.Excluded.Count switch
        {
            0 => string.Format(Localizer.Get("Export_NoticeAllFmt"), result.Included.Count),
            1 => string.Format(Localizer.Get("Export_NoticeOneFmt"),
                result.Included.Count, result.Excluded[0]),
            _ => string.Format(Localizer.Get("Export_NoticeFmt"),
                result.Included.Count, result.Excluded.Count, string.Join(", ", result.Excluded))
        };

        // Said out loud rather than hidden: the pack does come out different without a connection,
        // and a difference the user can see is one they can decide about.
        if (!result.AskedTheStore) text += " " + Localizer.Get("Export_NoticeOffline");
        return text;
    }
}
