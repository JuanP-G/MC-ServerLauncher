using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Localization;

namespace McServerLauncher.Views;

public partial class DeleteServerDialog : Window
{
    /// <summary>True if the user checked "also delete the files on disk".</summary>
    public bool DeleteFiles { get; private set; }

    /// <summary>True if the user checked "also delete its Playit tunnel".</summary>
    public bool DeleteTunnel { get; private set; }

    public DeleteServerDialog(string serverName, string folderPath)
    {
        InitializeComponent();
        MessageText.Text = string.Format(Localizer.Get("Del_ConfirmFmt"), serverName);
        FolderText.Text = $"{Localizer.Get("Ae_Folder")}: {folderPath}";
    }

    private void OnCheckChanged(object? sender, RoutedEventArgs e)
    {
        var on = DeleteFilesCheck.IsChecked == true;
        WarningBox.IsVisible = on;
        FolderText.IsVisible = on;
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        DeleteFiles = DeleteFilesCheck.IsChecked == true;
        DeleteTunnel = DeleteTunnelCheck.IsChecked == true;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
