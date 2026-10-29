using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Checks the installed mods for newer versions and replaces a jar with its newer one.
/// </summary>
/// <remarks>
/// <para>
/// The panel shows the outcome — the line under the list, the buttons — and this class does the work
/// they stand for. It is handed the rows because it marks them (<see cref="ModItem.Update"/>,
/// <see cref="ModItem.IsUpdating"/>), which is what the buttons bind to.
/// </para>
/// <para>
/// It keeps no cancellation token: every call that downloads takes the caller's, because the panel
/// replaces its own when the app closes and a copy captured here would stay cancelled for good.
/// </para>
/// </remarks>
internal sealed class ModUpdater
{
    /// <summary>What a check found.</summary>
    /// <param name="CouldAsk">False when the store did not answer, which says nothing about the mods.</param>
    /// <param name="Updates">How many rows now offer a newer version.</param>
    /// <param name="Hashes">The SHA-1 of every jar that could be read, for the scans that follow.</param>
    public sealed record CheckResult(bool CouldAsk, int Updates, IReadOnlyCollection<string> Hashes);

    /// <summary>What "update all" did.</summary>
    /// <param name="Updated">How many mods were updated.</param>
    /// <param name="Failed">The file names of the ones that could not be.</param>
    /// <param name="StoppedByServer">Whether it stopped because the running server holds the files.</param>
    public sealed record AllResult(int Updated, IReadOnlyList<string> Failed, bool StoppedByServer);

    /// <summary>What one replacement did, with the error text when it failed.</summary>
    public sealed record OneResult(ContentReplacement.UpdateOutcome Outcome, string? Error);

    private readonly ServerConfig _config;
    private readonly ModrinthService _store;
    private readonly InstalledContentIndex _index;
    private readonly PendingUpdateStore _pending;
    private readonly Action<string, string> _carryDisabledDate;

    /// <param name="config">The server whose content is updated.</param>
    /// <param name="store">Where the newer versions and their files come from.</param>
    /// <param name="index">What the store says each installed jar is.</param>
    /// <param name="pending">Where the newer versions a check found are kept between list rebuilds.</param>
    /// <param name="carryDisabledDate">
    /// Told when a disabled mod's file gets a new name (old path, new file name), so the month it has
    /// been off does not start again because it was updated meanwhile.
    /// </param>
    public ModUpdater(ServerConfig config, ModrinthService store, InstalledContentIndex index,
        PendingUpdateStore pending, Action<string, string> carryDisabledDate)
    {
        _config = config;
        _store = store;
        _index = index;
        _pending = pending;
        _carryDisabledDate = carryDisabledDate;
    }

    private string ModsFolder => Path.Combine(_config.FolderPath, ServerTypeCatalog.ContentFolder(_config.Type));

    /// <summary>
    /// Identifies each jar by its SHA-1 through Modrinth and flags the ones with a newer compatible
    /// version. Jars Modrinth doesn't know (CurseForge, hand-built) are left untouched.
    /// </summary>
    public async Task<CheckResult> CheckAsync(IReadOnlyCollection<ModItem> mods, CancellationToken ct)
    {
        _pending.Begin();

        // Map each jar's SHA-1 to its ModItem (disabled ones included) and clear any prior flag.
        var byHash = new Dictionary<string, ModItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            mod.Update = null;
            try
            {
                byHash[await _index.Hashes.Sha1Async(mod.FilePath, ct)] = mod;
            }
            catch { /* unreadable/locked file: skip it */ }
        }

        var latest = await _store.GetLatestVersionsByHashAsync(byHash.Keys, _config.Type,
            _config.GameVersion, ct);
        if (latest is null) return new CheckResult(CouldAsk: false, 0, byHash.Keys);

        var updates = 0;
        foreach (var (installedHash, version) in latest)
        {
            if (!byHash.TryGetValue(installedHash, out var mod)) continue;
            var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
            if (file is null) continue;

            // If the latest compatible version's file differs from the installed one, it's an update.
            if (string.Equals(file.Hashes?.Sha1, installedHash, StringComparison.OrdinalIgnoreCase)) continue;

            mod.Update = new ModUpdateInfo(version.VersionNumber, file.Url,
                Path.GetFileName(file.Filename), file.Hashes?.Sha512, file.Hashes?.Sha1,
                version.ProjectId);
            _pending.Remember(mod.FilePath, mod.Update);
            updates++;
        }

        return new CheckResult(CouldAsk: true, updates, byHash.Keys);
    }

    /// <summary>The line the update check leaves under the list.</summary>
    /// <remarks>
    /// "Everything is up to date" only when the store actually said so. Offline it used to print
    /// exactly that, which is a claim about every mod on the server made on the strength of no
    /// answer at all — and the one message nobody thinks to question.
    /// </remarks>
    public static string StatusText(bool couldAsk, int updates) =>
        !couldAsk ? Localizer.Get("Msg_UpdatesCheckFailed")
        : updates > 0 ? Localizer.Format("Msg_UpdatesFoundFmt", updates)
        : Localizer.Get("Msg_NoUpdates");

    /// <summary>The line "update all" leaves behind.</summary>
    public static string AllSummary(AllResult result) =>
        result.StoppedByServer
            ? Localizer.Format("Msg_UpdateAllStoppedFmt", result.Updated)
            : result.Failed.Count == 0
                ? Localizer.Format("Msg_UpdateAllDoneFmt", result.Updated)
                : Localizer.Format("Msg_UpdateAllPartialFmt",
                    result.Updated, result.Failed.Count, string.Join(", ", result.Failed));

    /// <summary>Updates every row in <paramref name="queue"/>, one after another.</summary>
    /// <remarks>
    /// One after another rather than all at once: each is a verified download followed by a file
    /// swap in the same folder, and doing them in parallel buys a few seconds at the cost of a
    /// progress line nobody could follow. A failure does not stop the rest — except a jar in use,
    /// which means the server is running and every other one would fail the same way.
    /// </remarks>
    /// <param name="queue">The rows that offer an update.</param>
    /// <param name="status">Told which one is under way ("2 of 5: name.jar").</param>
    /// <param name="ct">Cancels the downloads.</param>
    public async Task<AllResult> UpdateAllAsync(IReadOnlyList<ModItem> queue, IProgress<string> status,
        CancellationToken ct)
    {
        var updated = 0;
        var failed = new List<string>();
        var stoppedByServer = false;

        // Asked once, before the first download, rather than once per mod: the answer is about
        // the jars that were already here, and those are the only ones an update replaces.
        var folder = ModsFolder;
        var projects = Directory.Exists(folder)
            ? await _index.ProjectByPathAsync(folder, ct)
            : null;

        for (var i = 0; i < queue.Count; i++)
        {
            var mod = queue[i];

            // Already gone as another copy of a mod updated earlier in this run: what it was a
            // copy of is up to date now, and updating this one too would bring the duplicate back.
            if (!File.Exists(mod.FilePath)) continue;

            status.Report(Localizer.Format("Msg_UpdatingNofMFmt", i + 1, queue.Count, mod.FileName));

            var result = await UpdateOneAsync(mod, ct, projects);
            if (result.Outcome == ContentReplacement.UpdateOutcome.Updated)
            {
                updated++;
                continue;
            }

            failed.Add(mod.FileName);
            if (result.Outcome == ContentReplacement.UpdateOutcome.NeedsStop) { stoppedByServer = true; break; }
        }

        return new AllResult(updated, failed, stoppedByServer);
    }

    /// <summary>
    /// Replaces one jar with its newer version. Never leaves two versions of a mod, or none.
    /// </summary>
    /// <remarks>
    /// Shared by the row's button and by "update all", so there is one way a mod gets updated. It
    /// does not refresh the list — the callers do, once, when they are finished — because a
    /// refresh halfway through "update all" would rebuild the very rows it is walking.
    /// </remarks>
    /// <param name="mod">The row to update.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <param name="projectByPath">
    /// Which store project each jar already here is. "Update all" asks once and passes it to every
    /// mod; a single update asks for itself.
    /// </param>
    public async Task<OneResult> UpdateOneAsync(ModItem mod, CancellationToken ct,
        IReadOnlyDictionary<string, string>? projectByPath = null)
    {
        if (mod.Update is not { } update)
            return new OneResult(ContentReplacement.UpdateOutcome.Failed, null);

        mod.IsUpdating = true;
        try
        {
            var modsFolder = ModsFolder;
            Directory.CreateDirectory(modsFolder);
            var wasDisabled = !mod.IsEnabled;

            // Asked before downloading, while the folder still holds only what the user had.
            if (projectByPath is null && update.ProjectId is not null)
                projectByPath = await _index.ProjectByPathAsync(modsFolder, ct);

            // Download+verify the new jar under its own (enabled) name first, so a failure never
            // destroys the currently installed one.
            var enabledPath = AtomicDownload.PathIn(modsFolder, update.FileName);
            var replacesAFile = File.Exists(enabledPath);
            await _store.DownloadModAsync(update.Url, enabledPath, update.Sha512, update.Sha1, ct: ct);

            var (outcome, _) = ContentReplacement.ApplyDownloadedUpdate(modsFolder, enabledPath,
                mod.FilePath, wasDisabled, replacesAFile, update.ProjectId, projectByPath,
                _index.Manifests.Read);

            if (outcome == ContentReplacement.UpdateOutcome.Updated)
            {
                _pending.Forget(mod.FilePath);
                mod.Update = null;

                // The month it has been off does not start again because it was updated meanwhile.
                if (wasDisabled) _carryDisabledDate(mod.FilePath, update.FileName);
            }
            return new OneResult(outcome, null);
        }
        catch (Exception ex)
        {
            return new OneResult(ContentReplacement.UpdateOutcome.Failed, ErrorMessages.Describe(ex));
        }
        finally
        {
            mod.IsUpdating = false;
        }
    }
}
