namespace McServerLauncher.Models;

/// <summary>Una versión de Minecraft del manifiesto oficial de Mojang.</summary>
public class MinecraftVersion
{
    public string Id { get; set; } = string.Empty;

    /// <summary>"release" o "snapshot".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>URL al JSON de detalle de la versión (contiene la descarga del servidor).</summary>
    public string Url { get; set; } = string.Empty;

    public bool IsRelease => Type == "release";

    public override string ToString() => Id;
}
