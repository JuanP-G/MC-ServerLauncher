using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Manages a server's whitelist.json file (list of allowed players).
/// Each entry has uuid + name. It resolves the UUID from Mojang (online mode) or computes it
/// offline when the server does not use official accounts.
/// </summary>
public class WhitelistService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class Entry
    {
        public string uuid { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
    }

    private static string PathOf(string folder) => Path.Combine(folder, "whitelist.json");

    /// <summary>Returns the names of the players in the whitelist.</summary>
    public List<string> ReadNames(string folder)
    {
        var entries = ReadEntries(folder);
        return entries.Select(e => e.name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
    }

    private List<Entry> ReadEntries(string folder)
    {
        var path = PathOf(folder);
        if (!File.Exists(path)) return new List<Entry>();
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private void WriteEntries(string folder, List<Entry> entries)
        => File.WriteAllText(PathOf(folder), JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));

    /// <summary>Adds a player to whitelist.json (resolving their UUID). Does not duplicate.</summary>
    public async Task AddAsync(string folder, string name, bool onlineMode, CancellationToken ct = default)
    {
        var entries = ReadEntries(folder);
        if (entries.Any(e => e.name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        var uuid = onlineMode
            ? await ResolveOnlineUuidAsync(name, ct)
            : OfflineUuid(name);

        if (uuid is null)
            throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_PlayerNotFoundMojang"), name));

        entries.Add(new Entry { uuid = uuid, name = name });
        WriteEntries(folder, entries);
    }

    /// <summary>Removes a player (by name) from whitelist.json.</summary>
    public void Remove(string folder, string name)
    {
        var entries = ReadEntries(folder);
        var before = entries.Count;
        entries.RemoveAll(e => e.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entries.Count != before)
            WriteEntries(folder, entries);
    }

    /// <summary>Resolves the (dashed) UUID of a name via the Mojang API. Null if it doesn't exist.</summary>
    private async Task<string?> ResolveOnlineUuidAsync(string name, CancellationToken ct)
    {
        var resp = await Http.GetAsync($"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}", ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("id", out var idEl))
            return null;

        var id = idEl.GetString();
        return string.IsNullOrEmpty(id) ? null : Dash(id);
    }

    /// <summary>Offline UUID (same as Java: UUID v3 of "OfflinePlayer:&lt;name&gt;").</summary>
    public static string OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // variant
        return Dash(Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>Converts a 32-hex UUID into the dashed 8-4-4-4-12 format.</summary>
    private static string Dash(string hex32)
    {
        hex32 = hex32.Replace("-", "").ToLowerInvariant();
        if (hex32.Length != 32) return hex32;
        return $"{hex32[..8]}-{hex32[8..12]}-{hex32[12..16]}-{hex32[16..20]}-{hex32[20..]}";
    }
}
