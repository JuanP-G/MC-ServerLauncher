using CommunityToolkit.Mvvm.ComponentModel;
using McServerLauncher.Localization;

namespace McServerLauncher.Models;

/// <summary>Everything needed to update an installed mod to its latest Modrinth version.</summary>
public record ModUpdateInfo(string VersionNumber, string Url, string FileName, string? Sha512, string? Sha1);

public partial class ModItem : ObservableObject
{
    public string FilePath { get; }

    [ObservableProperty]
    private string _fileName;

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>Set by the update check when a newer version exists on Modrinth; null otherwise.</summary>
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
