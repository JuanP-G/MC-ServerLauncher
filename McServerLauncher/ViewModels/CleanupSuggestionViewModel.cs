using CommunityToolkit.Mvvm.ComponentModel;

namespace McServerLauncher.ViewModels;

/// <summary>One row of the "you can clean up" panel: a jar, why it is there, and whether it is ticked.</summary>
public partial class CleanupSuggestionViewModel : ObservableObject
{
    public CleanupSuggestionViewModel(string path, string fileName, string reason, string key)
    {
        Path = path;
        FileName = fileName;
        Reason = reason;
        Key = key;
    }

    /// <summary>The file on disk, <c>.disabled</c> included.</summary>
    public string Path { get; }

    /// <summary>The file name as the installed list shows it.</summary>
    public string FileName { get; }

    /// <summary>Why it is suggested, in a sentence.</summary>
    public string Reason { get; }

    /// <summary>What "not now" is remembered under.</summary>
    public string Key { get; }

    /// <summary>Ticked by default: the panel only lists what the app is confident about.</summary>
    [ObservableProperty]
    private bool _isSelected = true;
}
