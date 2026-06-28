using System.Windows;
using Wpf.Ui.Controls;

namespace McServerLauncher.Views;

public partial class DeleteServerDialog : FluentWindow
{
    /// <summary>True si el usuario marcó "eliminar también los archivos del disco".</summary>
    public bool DeleteFiles { get; private set; }

    /// <summary>True si el usuario marcó "eliminar también su túnel de Playit".</summary>
    public bool DeleteTunnel { get; private set; }

    public DeleteServerDialog(string serverName, string folderPath)
    {
        InitializeComponent();
        MessageText.Text = $"¿Eliminar el servidor \"{serverName}\" de la lista?";
        FolderText.Text = $"Carpeta: {folderPath}";
        DeleteFilesCheck.Checked += OnCheckChanged;
        DeleteFilesCheck.Unchecked += OnCheckChanged;
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e)
    {
        var on = DeleteFilesCheck.IsChecked == true;
        WarningBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FolderText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteFiles = DeleteFilesCheck.IsChecked == true;
        DeleteTunnel = DeleteTunnelCheck.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
