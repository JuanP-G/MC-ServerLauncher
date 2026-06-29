namespace McServerLauncher.Models;

/// <summary>A Minecraft version from Mojang's official manifest.</summary>
public class MinecraftVersion
{
    public string Id { get; set; } = string.Empty;

    /// <summary>"release" or "snapshot".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>URL to the version detail JSON (contains the server download).</summary>
    public string Url { get; set; } = string.Empty;

    public bool IsRelease => Type == "release";

    public override string ToString() => Id;
}
