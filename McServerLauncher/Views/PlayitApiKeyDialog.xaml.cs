using System.Windows;
using Wpf.Ui.Controls;

namespace McServerLauncher.Views;

public partial class PlayitApiKeyDialog : FluentWindow
{
    /// <summary>La clave introducida (válida si DialogResult == true).</summary>
    public string ApiKey { get; private set; } = string.Empty;

    public PlayitApiKeyDialog(string? current = null)
    {
        InitializeComponent();
        KeyBox.Text = current ?? string.Empty;
    }

    private void OpenPlayit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://playit.gg/account",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignorar si no hay navegador disponible.
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            System.Windows.MessageBox.Show("Pega tu clave de Playit.", "Clave de Playit",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        ApiKey = key;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
