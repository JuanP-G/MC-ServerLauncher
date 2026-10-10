using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>What the server runs on: how much memory it gets, and which Java.</summary>
public partial class PerformanceSection : UserControl, IServerSettingsSection
{
    private readonly ServerSettingsDraft _draft;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public PerformanceSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public PerformanceSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        _draft = draft;
        DataContext = draft.Config;
        MinRamBox.ValueChanged += (_, _) => ShowError(null);
        MaxRamBox.ValueChanged += (_, _) => ShowError(null);
    }

    /// <inheritdoc />
    public void Reload() { }

    /// <inheritdoc />
    public void ShowError(string? message)
    {
        Error.Text = message;
        Error.IsVisible = message is not null;
    }

    private async void BrowseJava_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectJava"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java")
                {
                    Patterns = OperatingSystem.IsWindows() ? new[] { "java.exe" } : new[] { "java" }
                }
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path)) _draft.Config.JavaPath = path;
    }
}
