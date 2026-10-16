using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace McServerLauncher.ViewModels;

/// <summary>
/// One row of the "you can clean up" panel: a jar, why it is there, and whether it is ticked.
/// </summary>
public partial class CleanupSuggestionViewModel : ObservableObject
{
    public CleanupSuggestionViewModel(string path, string fileName, string reason, string key)
    {
        Path = path;
        FileName = fileName;
        Reason = reason;
        Key = key;
        _scanned = Stamp(path);
    }

    /// <summary>The file as the scan saw it: its size and write time, or null if it was gone.</summary>
    private readonly (long Length, DateTime WrittenUtc)? _scanned;

    /// <summary>Whether the file is still the one the suggestion was made about.</summary>
    /// <remarks>
    /// The scan runs in the background and asks the store, so a row can outlive its file by seconds:
    /// a mod switched back on is renamed, an update can put a new jar under the same name. Deleting
    /// by path then removed nothing and said "removed" — or removed the new file.
    /// </remarks>
    public bool IsStillAsScanned => _scanned is not null && Stamp(Path) == _scanned;

    private static (long, DateTime)? Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
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
