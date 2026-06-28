using System.IO;
using System.Text.Json.Serialization;

namespace McServerLauncher.Models;

/// <summary>
/// Datos persistidos de un servidor de Minecraft registrado en la aplicación.
/// Se guardan en %APPDATA%\McServerLauncher\servers.json.
/// </summary>
public class ServerConfig
{
    /// <summary>Identificador estable (para no depender del nombre, que puede cambiar).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Nombre visible del servidor (p.ej. "Survival", "Modded").</summary>
    public string Name { get; set; } = "Nuevo servidor";

    /// <summary>Carpeta raíz del servidor (donde está el .jar y server.properties).</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Nombre del archivo .jar del servidor (relativo a la carpeta). Por defecto server.jar.</summary>
    public string JarFile { get; set; } = "server.jar";

    /// <summary>Ruta al ejecutable de Java. "java" usa el del PATH.</summary>
    public string JavaPath { get; set; } = "java";

    /// <summary>Memoria mínima en GB (-Xms).</summary>
    public int MinRamGb { get; set; } = 4;

    /// <summary>Memoria máxima en GB (-Xmx).</summary>
    public int MaxRamGb { get; set; } = 6;

    /// <summary>Argumentos extra de la JVM (opcional, p.ej. flags de GC).</summary>
    public string ExtraJvmArgs { get; set; } = string.Empty;

    // --- Playit.gg ---

    /// <summary>Si la integración con Playit.gg está activada para este servidor.</summary>
    public bool PlayitEnabled { get; set; }

    /// <summary>
    /// Dirección pública del túnel para este servidor. Se detecta automáticamente al ejecutar
    /// playit, pero también se puede escribir/pegar a mano y queda guardada.
    /// </summary>
    public string? TunnelAddress { get; set; }

    /// <summary>Ruta completa al .jar combinando carpeta + nombre del jar.</summary>
    [JsonIgnore]
    public string JarFullPath => Path.Combine(FolderPath, JarFile);

    /// <summary>Ruta al server.properties dentro de la carpeta del servidor.</summary>
    [JsonIgnore]
    public string PropertiesPath => Path.Combine(FolderPath, "server.properties");
}
