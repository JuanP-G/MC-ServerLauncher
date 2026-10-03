using CommunityToolkit.Mvvm.ComponentModel;
using McServerLauncher.Localization;

namespace McServerLauncher.Models;

/// <summary>Everything needed to update an installed mod to its latest Modrinth version.</summary>
public record ModUpdateInfo(string VersionNumber, string Url, string FileName, string? Sha512, string? Sha1);

/// <summary>
/// One mod or plugin file already sitting in a server's content folder, as the Mods tab shows it.
/// </summary>
/// <remarks>
/// <para>
/// A disabled item is the same file with <c>.disabled</c> appended.
/// </para>
/// <para>
/// Rows do not last: the Mods tab rebuilds the whole list from disk after every update, enable,
/// disable and delete. Anything that has to survive that — the newer version the last update check
/// found — is therefore kept by <c>ServerModsViewModel</c> and handed back to each rebuilt row, not
/// stored only here. Storing it only here is exactly how updating one mod used to make the buttons
/// on all the others disappear.
/// </para>
/// </remarks>
public partial class ModItem : ObservableObject
{
    /// <summary>Full path to the jar as it is on disk right now.</summary>
    public string FilePath { get; }

    [ObservableProperty]
    private string _fileName;

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>
    /// A newer version on Modrinth, or null. Set by the update check, and handed back by the panel to
    /// the row each time the list is rebuilt.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(UpdateTooltip))]
    private ModUpdateInfo? _update;

    [ObservableProperty]
    private bool _isUpdating;

    public bool UpdateAvailable => Update is not null;

    public string UpdateTooltip => Update is null
        ? string.Empty
        : string.Format(Localizer.Get("Tip_UpdateToFmt"), Update.VersionNumber);

    public ModItem(string filePath, string fileName, bool isEnabled)
    {
        FilePath = filePath;
        _fileName = fileName;
        _isEnabled = isEnabled;
    }
}
