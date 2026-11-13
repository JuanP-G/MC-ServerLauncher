using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Reads the notices file. Everything in it is text from the internet, even though it is signed: the
/// signature says who wrote it, not that what they wrote is well formed, so each field is checked,
/// bounded and cleaned before it can reach a screen.
/// </summary>
/// <remarks>
/// <para>
/// A file that is not a notices file at all (wrong schema, no <c>seq</c>, not JSON) throws; the
/// service keeps its last copy. A single bad <em>item</em> is skipped and the others stay: one typo
/// in a notice must not silence the rest, and must not make the app show something half-understood.
/// </para>
/// <para>
/// What is dropped rather than guessed matters most. An incident or security notice without an end
/// date is dropped (it would otherwise be a banner nobody can ever clear), and so is one that is
/// resolved without saying when (it could never be retired). A version bound that does not parse is
/// dropped too: ignoring it would show the notice to people it was not meant for.
/// </para>
/// </remarks>
public static class NewsFeedParser
{
    /// <summary>The only layout this build reads. Fields added later without changing it are ignored.</summary>
    public const int SupportedSchema = 1;

    /// <summary>The languages the app has, in the order the fallback tries them.</summary>
    internal static readonly string[] Languages = ["es", "en", "de", "fr", "pt"];

    internal const int MaxItems = 50;
    internal const int MaxIdLength = 64;
    internal const int MaxTitleLength = 80;
    internal const int MaxBodyLength = 400;

    /// <summary>
    /// The longest an incident or security notice can run from its start, whatever its <c>until</c>
    /// says. It is what keeps a forgotten notice from becoming permanent without anybody re-signing
    /// the file. The file has its own expiry too (<c>expires</c>), but that one says the file may be
    /// stale, not that a notice is over, and a notice it carries is not hidden when it passes.
    /// </summary>
    internal static readonly TimeSpan MaxUrgentSpan = TimeSpan.FromDays(30);

    /// <summary>The hosts a notice's "more information" link may point to: the project, its site and Playit.</summary>
    internal static readonly string[] LinkHosts =
        ["github.com", "playit.gg", "mc-server-launcher.vercel.app", "juanp-g.github.io"];

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8 };

    /// <summary>Reads a notices file.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a notices file this build understands.</exception>
    public static NewsFeed Parse(byte[] raw)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(raw, Options); }
        catch (JsonException ex) { throw new InvalidDataException("not JSON", ex); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not a notices file");
            if (!Int(root, "schema", out var schema) || schema != SupportedSchema)
                throw new InvalidDataException("a notices format this build does not know");
            if (!Int(root, "seq", out var seq) || seq < 1) throw new InvalidDataException("no seq");
            if (!Date(root, "issued", out var issued)) throw new InvalidDataException("no issued date");
            // Required, not optional: a file that never expires is the very thing this field is against,
            // and a reader that accepted one would let an old copy pass for current for ever.
            if (!Date(root, "expires", out var expires)) throw new InvalidDataException("no expires date");
            if (expires <= issued) throw new InvalidDataException("expires is not after issued");
            if (!root.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("no items");

            var items = new List<NewsItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in array.EnumerateArray())
            {
                if (items.Count >= MaxItems) break;
                if (ReadItem(element, issued) is not { } item) continue;
                if (seen.Add(item.Id)) items.Add(item);     // the first one with an id is the one that counts
            }
            return new NewsFeed(seq, issued, expires, items);
        }
    }

    private static NewsItem? ReadItem(JsonElement element, DateTimeOffset issued)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (!Str(element, "id", out var id) || id.Length is 0 or > MaxIdLength || !IdPattern.IsMatch(id))
            return null;

        if (!Str(element, "kind", out var kindText)) return null;
        NewsKind kind;
        switch (kindText)
        {
            case "incident": kind = NewsKind.Incident; break;
            case "security": kind = NewsKind.Security; break;
            case "tip": kind = NewsKind.Tip; break;
            default: return null;
        }
        var urgent = kind != NewsKind.Tip;

        // A tip has nothing to be resolved: it is listed while it is in the file.
        var status = NewsStatus.Active;
        if (urgent && element.TryGetProperty("status", out var statusElement))
        {
            if (statusElement.ValueKind != JsonValueKind.String) return null;
            switch (statusElement.GetString())
            {
                case "active": break;
                case "resolved": status = NewsStatus.Resolved; break;
                default: return null;
            }
        }

        var from = issued;
        if (element.TryGetProperty("from", out _) && !Date(element, "from", out from)) return null;

        DateTimeOffset? until = null;
        if (element.TryGetProperty("until", out _))
        {
            if (!Date(element, "until", out var parsedUntil)) return null;
            until = parsedUntil;
        }
        if (urgent && until is null) return null;
        if (until is { } end && end <= from) return null;
        if (urgent && until is { } limit && limit > from + MaxUrgentSpan) until = from + MaxUrgentSpan;

        DateTimeOffset? resolvedAt = null;
        if (urgent && status == NewsStatus.Resolved)
        {
            if (!Date(element, "resolvedAt", out var parsedResolved)) return null;
            resolvedAt = parsedResolved;
        }

        if (!ReadAudience(element, out var audience)) return null;

        string? link = null;
        if (Str(element, "link", out var rawLink) && AppHttp.IsAllowedUrl(rawLink, LinkHosts))
            link = rawLink;

        var text = ReadText(element);
        if (text.Count == 0) return null;

        return new NewsItem(id, kind, status, from, until, resolvedAt, audience, link, text);
    }

    private static bool ReadAudience(JsonElement element, out NewsAudience audience)
    {
        audience = NewsAudience.Everyone;
        if (!element.TryGetProperty("only", out var only)) return true;
        if (only.ValueKind != JsonValueKind.Object) return false;

        bool? tunnels = null;
        if (only.TryGetProperty("usesTunnels", out var tunnelsElement))
        {
            if (tunnelsElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            tunnels = tunnelsElement.GetBoolean();
        }

        var systems = new List<string>();
        if (only.TryGetProperty("os", out var osElement))
        {
            if (osElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var name in osElement.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String) return false;
                var clean = UntrustedText.Clean(name.GetString(), 16);
                if (clean is not null) systems.Add(clean.ToLowerInvariant());
            }
            // "os": [] written on purpose would mean nobody; that is a mistake, not a notice.
            if (systems.Count == 0) return false;
        }

        if (!Bound(only, "minVersion", out var min) || !Bound(only, "maxVersion", out var max)) return false;

        audience = new NewsAudience(tunnels, systems, min, max);
        return true;
    }

    /// <summary>A version bound: absent is fine, present but unreadable is not.</summary>
    private static bool Bound(JsonElement element, string name, out Version? bound)
    {
        bound = null;
        if (!element.TryGetProperty(name, out var value)) return true;
        return value.ValueKind == JsonValueKind.String && Version.TryParse(value.GetString(), out bound);
    }

    private static Dictionary<string, NewsText> ReadText(JsonElement element)
    {
        var text = new Dictionary<string, NewsText>(StringComparer.Ordinal);
        if (!element.TryGetProperty("text", out var all) || all.ValueKind != JsonValueKind.Object) return text;

        foreach (var language in Languages)
        {
            if (!all.TryGetProperty(language, out var words) || words.ValueKind != JsonValueKind.Object) continue;

            var title = Str(words, "title", out var rawTitle) ? UntrustedText.Clean(rawTitle, MaxTitleLength) : null;
            if (title is null) continue;
            var body = Str(words, "body", out var rawBody) ? UntrustedText.Clean(rawBody, MaxBodyLength) : null;
            text[language] = new NewsText(title, body ?? string.Empty);
        }
        return text;
    }

    private static bool Str(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool Int(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    /// <summary>A moment, ISO 8601; with no offset it is read as UTC rather than as the reader's local time.</summary>
    private static bool Date(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return Str(element, name, out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}
