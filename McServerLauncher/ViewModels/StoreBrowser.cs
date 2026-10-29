using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Asks the store for a page of results, and keeps track of which page comes next and which of two
/// overlapping searches is the one that counts.
/// </summary>
/// <remarks>
/// <para>
/// The panel owns what is on screen — the text box, the chips, the list — and hands this the
/// values; the answer comes back as a <see cref="LoadResult"/> to be drawn. Nothing here touches a
/// control or a collection that a control is bound to.
/// </para>
/// <para>
/// Every new search supersedes the one before it. Without that, two quick clicks on two chips race:
/// both responses land on the same list, the paging offset is advanced twice and "load more"
/// silently skips a page.
/// </para>
/// </remarks>
internal sealed class StoreBrowser
{
    /// <summary>How many results one page asks for.</summary>
    public const int PageSize = 20;

    /// <summary>How a load ended.</summary>
    public enum Kind
    {
        /// <summary>The store answered: <see cref="LoadResult.Response"/> holds the page.</summary>
        Page,

        /// <summary>The store did not answer. For "load more" that means nothing changes.</summary>
        NoAnswer,

        /// <summary>Cancelled, or a newer search started: the one that replaced it owns the screen.</summary>
        Superseded,

        /// <summary>Something went wrong; <see cref="LoadResult.Error"/> says what.</summary>
        Failed,
    }

    /// <summary>What a load found.</summary>
    /// <param name="Kind">How it ended.</param>
    /// <param name="Response">The page, for <see cref="Kind.Page"/>.</param>
    /// <param name="Error">The description of the error, for <see cref="Kind.Failed"/>.</param>
    /// <param name="StillInCharge">
    /// Whether this is the search the interface is waiting for. Only that one may clear the spinner.
    /// </param>
    public sealed record LoadResult(Kind Kind, SearchResponse? Response = null, string? Error = null,
        bool StillInCharge = true);

    private readonly ServerConfig _config;
    private readonly ModrinthService _store;

    /// <summary>Cancels the search in flight when a newer one starts.</summary>
    private CancellationTokenSource? _current;

    private int _offset;

    /// <param name="config">The server whose loader and Minecraft version every search is limited to.</param>
    /// <param name="store">Answers the searches.</param>
    public StoreBrowser(ServerConfig config, ModrinthService store)
    {
        _config = config;
        _store = store;
    }

    /// <summary>
    /// Whether this server has anything to search for. Vanilla has no mods, and a server with no
    /// version yet has nothing to be compatible with.
    /// </summary>
    public bool CanSearch => _config.Type != ServerType.Vanilla && !string.IsNullOrEmpty(_config.GameVersion);

    /// <summary>The store's name for a sort choice: 0 is relevance, 1 is downloads.</summary>
    public static string SortIndex(int selectedSort) => selectedSort == 1 ? "downloads" : "relevance";

    /// <summary>
    /// Turns the text box and the chosen categories into what the store is asked.
    /// </summary>
    /// <remarks>
    /// Facet-backed categories stack into separate groups, which Modrinth ANDs. The ones with no
    /// facet contribute their words to the query instead (only ever one of those at a time).
    /// </remarks>
    public static (string Query, List<string> Facets) BuildQuery(string? text,
        IEnumerable<StoreTagDefinition> tags)
    {
        var chosen = tags.ToList();
        var facets = chosen.SelectMany(t => t.Facets).ToList();
        var query = text ?? string.Empty;
        foreach (var loose in chosen.Where(t => t.Facets.Count == 0))
            if (loose.Query is { Length: > 0 } words)
                query = string.IsNullOrWhiteSpace(query) ? words : query + " " + words;
        return (query, facets);
    }

    /// <summary>
    /// Loads a page for the current loader and version. When <paramref name="append"/> is false the
    /// paging starts over (a new search or sort); when true the next page is wanted.
    /// With an empty query Modrinth returns the top mods, so the browser works without searching.
    /// </summary>
    /// <param name="text">The search box.</param>
    /// <param name="tags">The categories the user picked, in order.</param>
    /// <param name="selectedSort">The sort choice; see <see cref="SortIndex"/>.</param>
    /// <param name="side">Which side of the game the results run on.</param>
    /// <param name="append">True for "load more".</param>
    /// <param name="ct">The caller's token; for a new search it is linked to the supersede token.</param>
    public async Task<LoadResult> LoadAsync(string? text, IEnumerable<StoreTagDefinition> tags,
        int selectedSort, StoreSideFilter side, bool append, CancellationToken ct)
    {
        CancellationTokenSource? mine = null;
        if (!append)
        {
            _current?.Cancel();
            _current?.Dispose();
            mine = _current = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ct = mine.Token;
            _offset = 0;
        }

        var (query, facets) = BuildQuery(text, tags);

        // Whether this search is still the one the interface is waiting for.
        bool InCharge() => append || mine is null || ReferenceEquals(_current, mine);

        try
        {
            var response = await _store.SearchModsAsync(query, _config.Type, _config.GameVersion,
                SortIndex(selectedSort), _offset, PageSize, facets.Count > 0 ? facets : null, ct, side);

            // A newer search started while this one was in flight: its results are the ones that
            // match what the interface is showing, so drop these.
            if (ct.IsCancellationRequested || (mine is not null && !ReferenceEquals(_current, mine)))
                return new LoadResult(Kind.Superseded, StillInCharge: InCharge());

            if (response is null) return new LoadResult(Kind.NoAnswer, StillInCharge: InCharge());

            _offset += response.Hits.Count;
            return new LoadResult(Kind.Page, response, StillInCharge: InCharge());
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search; the one that replaced it owns the UI now.
            return new LoadResult(Kind.Superseded, StillInCharge: InCharge());
        }
        catch (Exception ex)
        {
            return new LoadResult(Kind.Failed, Error: ErrorMessages.Describe(ex), StillInCharge: InCharge());
        }
    }

    /// <summary>Stops whatever search is in flight and forgets it, for when the app closes.</summary>
    public void CancelPending()
    {
        try { _current?.Cancel(); } catch { /* already disposed */ }
        _current?.Dispose();
        _current = null;
    }
}
