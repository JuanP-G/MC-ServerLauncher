using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>
/// Gestiona el archivo whitelist.json de un servidor (lista de jugadores permitidos).
/// Cada entrada lleva uuid + nombre. Resuelve el UUID desde Mojang (modo online) o lo calcula
/// de forma offline cuando el servidor no usa cuentas oficiales.
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

    /// <summary>Devuelve los nombres de los jugadores en la whitelist.</summary>
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

    /// <summary>Añade un jugador a whitelist.json (resolviendo su UUID). No duplica.</summary>
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
                $"No se encontró el jugador '{name}' en Mojang. Revisa que el nombre esté bien escrito.");

        entries.Add(new Entry { uuid = uuid, name = name });
        WriteEntries(folder, entries);
    }

    /// <summary>Quita un jugador (por nombre) de whitelist.json.</summary>
    public void Remove(string folder, string name)
    {
        var entries = ReadEntries(folder);
        var before = entries.Count;
        entries.RemoveAll(e => e.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entries.Count != before)
            WriteEntries(folder, entries);
    }

    /// <summary>Resuelve el UUID (con guiones) de un nombre vía la API de Mojang. Null si no existe.</summary>
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

    /// <summary>UUID offline (igual que Java: UUID v3 de "OfflinePlayer:&lt;name&gt;").</summary>
    public static string OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30); // versión 3
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // variante
        return Dash(Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>Convierte un UUID de 32 hex en formato con guiones 8-4-4-4-12.</summary>
    private static string Dash(string hex32)
    {
        hex32 = hex32.Replace("-", "").ToLowerInvariant();
        if (hex32.Length != 32) return hex32;
        return $"{hex32[..8]}-{hex32[8..12]}-{hex32[12..16]}-{hex32[16..20]}-{hex32[20..]}";
    }
}
