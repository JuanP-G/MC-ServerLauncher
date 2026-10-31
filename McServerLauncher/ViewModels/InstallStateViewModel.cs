using CommunityToolkit.Mvvm.ComponentModel;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;

namespace McServerLauncher.ViewModels;

/// <summary>
/// What a project's button has to know: is it installed, can it be updated, is it switched off.
/// Shared by the card in the results and the details page, so both say the same thing and the one
/// <c>InstallStateButton</c> control binds to either.
/// </summary>
/// <remarks>
/// The panel decides the state (<see cref="ServerModsViewModel"/> asks the
/// <see cref="Services.InstallStateResolver"/>) and hands it down; nothing here looks at the disk.
/// </remarks>
public abstract partial class InstallStateViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(IsUpdatable))]
    [NotifyPropertyChangedFor(nameof(IsDisabled))]
    private InstallState _state;

    public bool IsNotInstalled => State == InstallState.NotInstalled;

    public bool IsInstalled => State == InstallState.Installed;

    public bool IsUpdatable => State == InstallState.UpdateAvailable;

    public bool IsDisabled => State == InstallState.Disabled;

    /// <summary>The build the update button installs, when there is one.</summary>
    protected VersionResult? Newer { get; private set; }

    /// <summary>What the install button says: where it is going, when that is not the server.</summary>
    public abstract string InstallLabel { get; }

    /// <summary>Takes the panel's answer. The build goes first so a button never fires with the old one.</summary>
    internal void ApplyState(ProjectState state)
    {
        Newer = state.Newer;
        State = state.State;
    }
}
