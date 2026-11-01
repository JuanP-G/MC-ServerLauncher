using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// What each button in the store says: Install, Update, Installed, or switch on.
/// </summary>
/// <remarks>
/// <para>
/// The answer comes from the folders, not from anything the panel remembers having installed — a mod
/// copied in by hand counts, and one deleted from the file manager stops counting. It is worked out
/// by <see cref="InstallStateResolver"/> and handed to the cards and to the open details page.
/// </para>
/// <para>
/// <b>Nothing is asked until something is on screen to show it.</b> The panel is also built for
/// servers whose tab is never opened and by tests that must not reach the store, and
/// <see cref="RefreshInstalledMods"/> runs for all of them.
/// </para>
/// </remarks>
public partial class ServerModsViewModel
{
    /// <summary>Every project with a jar in the server's folder, by project id.</summary>
    private IReadOnlyDictionary<string, ProjectState> _serverStates = new Dictionary<string, ProjectState>();

    /// <summary>The same for the players' pack, where the client-only mods go.</summary>
    private IReadOnlyDictionary<string, ProjectState> _packStates = new Dictionary<string, ProjectState>();

    /// <summary>The check in charge; a newer one cancels it.</summary>
    private CancellationTokenSource? _stateCheck;

    /// <summary>The last check started, for whoever needs to wait for its answer.</summary>
    private Task _stateTask = Task.CompletedTask;

    /// <summary>Completes when the check that is running — or ran last — has put its answer on screen.</summary>
    internal Task InstallStatesSettled => _stateTask;

    /// <summary>
    /// What the button of <paramref name="projectId"/> should say. A client-only mod is looked for in
    /// the players' pack, because that is where installing it puts it.
    /// </summary>
    internal ProjectState StateOf(string projectId, bool clientOnly) =>
        (clientOnly ? _packStates : _serverStates).TryGetValue(projectId, out var state)
            ? state
            : ProjectState.None;

    /// <summary>Starts a check, unless nothing is showing a project.</summary>
    /// <param name="force">Start it anyway, for the moment the tab is first shown.</param>
    private void RefreshInstallStates(bool force = false)
    {
        if (!force && SearchResults.Count == 0 && Details is null) return;
        _stateTask = CheckInstallStatesAsync();
    }

    private async Task CheckInstallStatesAsync()
    {
        _stateCheck?.Cancel();
        _stateCheck?.Dispose();
        var mine = _stateCheck = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        try
        {
            var server = await _states.ResolveAsync(Path.Combine(_config.FolderPath, ContentFolder),
                mine.Token);
            // A plugin server has no players' pack: there is nothing to look in.
            var pack = IsPluginBased
                ? new Dictionary<string, ProjectState>()
                : await _states.ResolveAsync(ClientPack.FolderOf(_config), mine.Token);

            // A newer check started while this one waited: its answer is the one about the folder as
            // it is now.
            if (!ReferenceEquals(_stateCheck, mine)) return;

            _serverStates = server;
            _packStates = pack;
            ApplyInstallStates();
        }
        catch (OperationCanceledException)
        {
            // Superseded, or the app is closing.
        }
        catch (Exception)
        {
            // Best effort: the buttons keep what they said, and the install itself still resolves
            // everything it needs. A check that failed must not take the panel down with it.
        }
    }

    /// <summary>Hands the current answers to the cards on screen and to the details page.</summary>
    private void ApplyInstallStates()
    {
        foreach (var card in SearchResults)
            card.ApplyState(StateOf(card.Id, card.IsClientOnly));
        Details?.RefreshState();
    }

    /// <summary>
    /// Switches on the disabled copy of a project, the way the installed list's switch does.
    /// </summary>
    internal void EnableFromCard(string projectId)
    {
        // Only the server's folder: the players' pack has no switch, and its jars never show Disabled.
        if (!_serverStates.TryGetValue(projectId, out var state)
            || state.State != InstallState.Disabled || state.Path is null)
            return;

        var mod = InstalledMods.FirstOrDefault(m =>
            string.Equals(m.FilePath, state.Path, StringComparison.OrdinalIgnoreCase));
        if (mod is not null) ToggleMod(mod);
    }
}
