namespace McServerLauncher.Models;

/// <summary>Global application settings (not per-server).</summary>
public class AppSettings
{
    /// <summary>
    /// Playit key with write permission, used to create/delete tunnels.
    /// (The agent key in playit.toml is read-only and is not valid for this.)
    /// </summary>
    public string? PlayitApiKey { get; set; }

    /// <summary>UI language (es, en, pt, fr, de). Empty = system language.</summary>
    public string? Language { get; set; }

    /// <summary>Last app version the user has already seen (to show the what's-new screen after updating).</summary>
    public string? LastVersionSeen { get; set; }
}
