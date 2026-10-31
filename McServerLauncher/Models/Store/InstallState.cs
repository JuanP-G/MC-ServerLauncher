using McServerLauncher.Models.Modrinth;

namespace McServerLauncher.Models.Store;

/// <summary>Where a store project stands on this server, as the browser shows it.</summary>
public enum InstallState
{
    /// <summary>No jar in the folder is this project.</summary>
    NotInstalled,

    /// <summary>It is there and it is the newest build for this server — or the store could not say otherwise.</summary>
    Installed,

    /// <summary>It is there and the store has a newer build for this server.</summary>
    UpdateAvailable,

    /// <summary>Every copy of it is switched off (<c>.jar.disabled</c>).</summary>
    Disabled,
}

/// <summary>
/// One project's state, with what acting on it needs.
/// </summary>
/// <param name="State">What to show.</param>
/// <param name="Newer">The build to install, for <see cref="InstallState.UpdateAvailable"/>.</param>
/// <param name="Path">
/// The jar the state is about: the enabled one, or the disabled one for
/// <see cref="InstallState.Disabled"/>. Null when nothing is installed.
/// </param>
public sealed record ProjectState(InstallState State, VersionResult? Newer = null, string? Path = null)
{
    /// <summary>What a project nobody installed looks like.</summary>
    public static ProjectState None { get; } = new(InstallState.NotInstalled);
}
