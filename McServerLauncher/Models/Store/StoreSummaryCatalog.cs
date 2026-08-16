using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace McServerLauncher.Models.Store;

/// <summary>
/// The hand-written plain-language summaries, generated into
/// <c>Resources/store-summaries.json</c> by <c>tools/generate-store-summaries.py</c>.
/// <para>
/// Keyed by Modrinth project id, because ids are permanent and slugs can be renamed. Each entry is
/// a flat map so a new language is a change to the data file and the generator, not to this code:
/// the reserved keys are <c>slug</c> and <c>title</c>, everything else is a language code.
/// </para>
/// </summary>
/// <summary>
/// What to print about a project: the summary in the user's language, and the author's own line as
/// a secondary note when it adds something. <see cref="Tagline"/> is empty when there is nothing
/// worth showing, and the view hides the row rather than printing a blank line.
/// </summary>
public readonly record struct StoreBlurb(string Summary, string Tagline);

public class StoreSummaryCatalog
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("summaries")]
    public Dictionary<string, Dictionary<string, string>> Summaries { get; set; } = new();
}
