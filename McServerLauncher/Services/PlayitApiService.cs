using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McServerLauncher.Services;

/// <summary>Error devuelto por la API de Playit (status != success).</summary>
public class PlayitApiException : Exception
{
    public string? ErrorType { get; }
    public bool IsAuthError => ErrorType == "auth";

    public PlayitApiException(string? type, string message) : base(message) => ErrorType = type;
}

/// <summary>
/// Cliente de la API de Playit.gg.
/// - Lecturas (listar túneles / dirección): usan el secret_key del agente (playit.toml), que suele
///   ser de solo lectura.
/// - Escrituras (crear/eliminar túnel): requieren una clave con permiso de escritura que aporta el
///   usuario (se envía como Api-Key, con respaldo Agent-Key).
/// </summary>
public class PlayitApiService
{
    private const string BaseUrl = "https://api.playit.gg";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly string[] TomlPaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "playit_gg", "playit.toml"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "playit_gg", "playit.toml"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "playit_gg", "playit.toml"),
    };

    public record PlayitTunnel(string Id, string Name, int LocalPort, string? AssignedDomain, string? CustomDomain)
    {
        public string? Address => string.IsNullOrEmpty(CustomDomain) ? AssignedDomain : CustomDomain;
    }

    /// <summary>Lee el secret_key (de solo lectura) de playit.toml. Null si no se encuentra.</summary>
    public string? ReadSecretKey()
    {
        foreach (var path in TomlPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("secret_key", StringComparison.OrdinalIgnoreCase)) continue;
                    var idx = t.IndexOf('=');
                    if (idx > 0) return t[(idx + 1)..].Trim().Trim('"');
                }
            }
            catch { /* probar siguiente ruta */ }
        }
        return null;
    }

    private async Task<JsonElement> PostAsync(string path, string authValue, string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
        req.Headers.TryAddWithoutValidation("Authorization", authValue);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if ((root.TryGetProperty("status", out var s) ? s.GetString() : null) != "success")
        {
            string? type = null, msg = json;
            if (root.TryGetProperty("data", out var d))
            {
                type = d.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                msg = d.TryGetProperty("message", out var m) ? m.GetString() ?? d.ToString() : d.ToString();
            }
            throw new PlayitApiException(type, msg ?? "error desconocido");
        }
        return root.GetProperty("data").Clone();
    }

    /// <summary>POST de escritura: prueba con Api-Key y, si hay error de auth, reintenta con Agent-Key.</summary>
    private async Task<JsonElement> PostWriteAsync(string path, string writeKey, string body, CancellationToken ct)
    {
        try
        {
            return await PostAsync(path, $"Api-Key {writeKey}", body, ct);
        }
        catch (PlayitApiException ex) when (ex.IsAuthError)
        {
            return await PostAsync(path, $"Agent-Key {writeKey}", body, ct);
        }
    }

    /// <summary>Lee agent_id y túneles usando una clave (por defecto la de solo lectura del agente).</summary>
    public async Task<(string AgentId, List<PlayitTunnel> Tunnels)> GetRunDataAsync(string readAuthValue, CancellationToken ct = default)
    {
        var data = await PostAsync("/agents/rundata", readAuthValue, "", ct);
        var agentId = data.TryGetProperty("agent_id", out var a) ? a.GetString() ?? "" : "";

        var list = new List<PlayitTunnel>();
        if (data.TryGetProperty("tunnels", out var tunnels) && tunnels.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tunnels.EnumerateArray())
            {
                var id = t.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var localPort = t.TryGetProperty("local_port", out var lp) && lp.TryGetInt32(out var p) ? p : 0;
                var assigned = t.TryGetProperty("assigned_domain", out var ad) ? ad.GetString() : null;
                var custom = t.TryGetProperty("custom_domain", out var cd) ? cd.GetString() : null;
                list.Add(new PlayitTunnel(id, name, localPort, assigned, custom));
            }
        }
        return (agentId, list);
    }

    /// <summary>Dirección pública del túnel cuyo puerto local coincide con <paramref name="port"/>, o null.</summary>
    public async Task<string?> GetAddressForPortAsync(int port, CancellationToken ct = default)
    {
        var secret = ReadSecretKey();
        if (string.IsNullOrEmpty(secret)) return null;

        var (_, tunnels) = await GetRunDataAsync($"agent-key {secret}", ct);
        return tunnels.FirstOrDefault(t => t.LocalPort == port)?.Address;
    }

    /// <summary>Devuelve el authValue para leer (clave del agente si existe, si no la de escritura).</summary>
    private string? ReadAuth(string writeKey)
    {
        var secret = ReadSecretKey();
        if (!string.IsNullOrEmpty(secret)) return $"agent-key {secret}";
        return string.IsNullOrEmpty(writeKey) ? null : $"Api-Key {writeKey}";
    }

    /// <summary>
    /// Crea el túnel Minecraft Java del servidor si no existe ya uno para ese puerto.
    /// Devuelve true si lo creó, false si ya existía. Requiere clave de escritura.
    /// </summary>
    public async Task<bool> EnsureMinecraftTunnelAsync(string writeKey, string name, int localPort, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(writeKey))
            throw new InvalidOperationException("Falta la clave de escritura de Playit.");

        var readAuth = ReadAuth(writeKey)
            ?? throw new InvalidOperationException("No se pudo autenticar con Playit para leer los túneles.");

        var (agentId, tunnels) = await GetRunDataAsync(readAuth, ct);
        if (tunnels.Any(t => t.LocalPort == localPort))
            return false;

        var body = new JsonObject
        {
            ["name"] = name,
            ["tunnel_type"] = "minecraft-java",
            ["port_type"] = "tcp",
            ["port_count"] = 1,
            ["enabled"] = true,
            ["origin"] = new JsonObject
            {
                ["type"] = "agent",
                ["data"] = new JsonObject
                {
                    ["agent_id"] = agentId,
                    ["local_ip"] = "127.0.0.1",
                    ["local_port"] = localPort
                }
            }
        }.ToJsonString();

        await PostWriteAsync("/tunnels/create", writeKey, body, ct);
        return true;
    }

    /// <summary>Elimina el túnel cuyo puerto local coincide. Devuelve true si borró alguno. Requiere clave de escritura.</summary>
    public async Task<bool> DeleteTunnelForPortAsync(string writeKey, int localPort, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(writeKey))
            throw new InvalidOperationException("Falta la clave de escritura de Playit.");

        var readAuth = ReadAuth(writeKey)
            ?? throw new InvalidOperationException("No se pudo autenticar con Playit para leer los túneles.");

        var (_, tunnels) = await GetRunDataAsync(readAuth, ct);
        var match = tunnels.FirstOrDefault(t => t.LocalPort == localPort);
        if (match is null || string.IsNullOrEmpty(match.Id)) return false;

        var body = new JsonObject { ["tunnel_id"] = match.Id }.ToJsonString();
        await PostWriteAsync("/tunnels/delete", writeKey, body, ct);
        return true;
    }
}
