using System.IO;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Lectura del archivo server.properties (formato key=value, una por línea).
/// En el MVP sólo se usa para leer (p.ej. el puerto). La escritura llegará en la fase
/// de "Configuración visual".
/// </summary>
public class ServerPropertiesService
{
    /// <summary>
    /// Lee server.properties y devuelve un diccionario clave→valor.
    /// Devuelve vacío si el archivo no existe.
    /// </summary>
    public Dictionary<string, string> Read(string propertiesPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(propertiesPath))
            return result;

        foreach (var raw in File.ReadAllLines(propertiesPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var idx = line.IndexOf('=');
            if (idx <= 0)
                continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            result[key] = value;
        }

        return result;
    }

    /// <summary>Devuelve el puerto del servidor (server-port) o null si no se encuentra.</summary>
    public int? GetServerPort(string propertiesPath)
    {
        var props = Read(propertiesPath);
        if (props.TryGetValue("server-port", out var value) && int.TryParse(value, out var port))
            return port;
        return null;
    }

    /// <summary>
    /// Actualiza las claves indicadas en server.properties conservando el resto de líneas,
    /// comentarios y orden. Las claves nuevas se añaden al final. Crea el archivo si no existe.
    /// </summary>
    public void Update(string propertiesPath, IDictionary<string, string> changes)
    {
        var lines = File.Exists(propertiesPath)
            ? File.ReadAllLines(propertiesPath).ToList()
            : new List<string>();

        var remaining = new Dictionary<string, string>(changes, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var idx = lines[i].IndexOf('=');
            if (idx <= 0)
                continue;

            var key = lines[i][..idx].Trim();
            if (remaining.TryGetValue(key, out var value))
            {
                lines[i] = $"{key}={value}";
                remaining.Remove(key);
            }
        }

        foreach (var kv in remaining)
            lines.Add($"{kv.Key}={kv.Value}");

        File.WriteAllLines(propertiesPath, lines, new UTF8Encoding(false));
    }
}
