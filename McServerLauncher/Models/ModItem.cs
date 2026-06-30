using CommunityToolkit.Mvvm.ComponentModel;

namespace McServerLauncher.Models;

public partial class ModItem : ObservableObject
{
    public string FilePath { get; }
    
    [ObservableProperty]
    private string _fileName;
    
    [ObservableProperty]
    private bool _isEnabled;

    public ModItem(string filePath, string fileName, bool isEnabled)
    {
        FilePath = filePath;
        _fileName = fileName;
        _isEnabled = isEnabled;
    }
}
