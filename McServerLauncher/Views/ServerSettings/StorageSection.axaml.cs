using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>What the server takes on disk, by part, with the way to its folder.</summary>
/// <remarks>
/// Reads the server's own measurement (<see cref="ServerViewModel.DiskUsage"/>) rather than the
/// draft: none of it is a setting. Opening the page measures again, since whoever opens it wants the
/// number as it is now.
/// </remarks>
public partial class StorageSection : UserControl, IServerSettingsSection
{
    private readonly ServerViewModel _server;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public StorageSection() : this(new ServerViewModel(new ServerConfig())) { }

    public StorageSection(ServerViewModel server)
    {
        InitializeComponent();
        _server = server;
        DataContext = server;
        AttachedToVisualTree += (_, _) =>
        {
            _server.PropertyChanged += OnServerChanged;
            FillBar();
            _ = _server.MeasureDiskAsync();
        };
        DetachedFromVisualTree += (_, _) => _server.PropertyChanged -= OnServerChanged;
    }

    private void OnServerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerViewModel.DiskUsage)) FillBar();
    }

    /// <summary>One piece per part, as wide as its share, in the colour of its row below.</summary>
    private void FillBar()
    {
        Bar.ColumnDefinitions.Clear();
        Bar.Children.Clear();
        var parts = new (double Share, string Brush)[]
        {
            (_server.DiskWorldShare, "SemanticOk"),
            (_server.DiskBackupsShare, "SemanticInfo"),
            (_server.DiskContentShare, "SemanticWarn"),
            (_server.DiskLogsShare, "SemanticDanger"),
            (_server.DiskOtherShare, "McListText"),
        };
        foreach (var (share, brush) in parts)
        {
            if (share <= 0) continue;
            Bar.ColumnDefinitions.Add(new ColumnDefinition(share, GridUnitType.Star));
            var piece = new Border
            {
                Background = this.FindResource(brush) as IBrush,
            };
            Grid.SetColumn(piece, Bar.ColumnDefinitions.Count - 1);
            Bar.Children.Add(piece);
        }
    }

    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = _server.Config.FolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    /// <inheritdoc />
    public void Reload() { }

    /// <inheritdoc />
    public void ShowError(string? message) { }
}
