namespace McServerLauncher.Models;

/// <summary>Ajustes globales de la aplicación (no por servidor).</summary>
public class AppSettings
{
    /// <summary>
    /// Clave de Playit con permiso de escritura, para crear/eliminar túneles.
    /// (La clave del agente en playit.toml es de solo lectura y no sirve para esto.)
    /// </summary>
    public string? PlayitApiKey { get; set; }

    /// <summary>Idioma de la interfaz (es, en, pt, fr, de). Vacío = idioma del sistema.</summary>
    public string? Language { get; set; }
}
