namespace McServerLauncher.Models;

/// <summary>Ajustes globales de la aplicación (no por servidor).</summary>
public class AppSettings
{
    /// <summary>
    /// Clave de Playit con permiso de escritura, para crear/eliminar túneles.
    /// (La clave del agente en playit.toml es de solo lectura y no sirve para esto.)
    /// </summary>
    public string? PlayitApiKey { get; set; }
}
