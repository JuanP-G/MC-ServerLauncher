using System.IO;
using System.Text.Json;
using System.Windows;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace McServerLauncher.Views;

public partial class AddEditServerDialog : FluentWindow
{
    private readonly ServerConfig _config;
    private readonly string _snapshot;

    public AddEditServerDialog(ServerConfig config)
    {
        InitializeComponent();
        _config = config;
        // Guardamos una copia para restaurar si el usuario cancela.
        _snapshot = JsonSerializer.Serialize(config);
        DataContext = _config;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Localizer.Get("Title_SelectServerFolder") };
        if (Directory.Exists(_config.FolderPath))
            dialog.InitialDirectory = _config.FolderPath;

        if (dialog.ShowDialog() == true)
        {
            _config.FolderPath = dialog.FolderName;
            DataContext = null;
            DataContext = _config;

            // Si el nombre sigue siendo el por defecto, sugerimos el de la carpeta.
            if (string.IsNullOrWhiteSpace(_config.Name) || _config.Name == "Nuevo servidor")
            {
                _config.Name = new DirectoryInfo(dialog.FolderName).Name;
                DataContext = null;
                DataContext = _config;
            }
        }
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Localizer.Get("Title_SelectJava"),
            Filter = Localizer.Get("Filter_Java")
        };
        if (dialog.ShowDialog() == true)
        {
            _config.JavaPath = dialog.FileName;
            DataContext = null;
            DataContext = _config;
        }
    }


    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.Name))
        {
            System.Windows.MessageBox.Show(Localizer.Get("Msg_NameEmpty"), Localizer.Get("Title_Validation"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(_config.FolderPath))
        {
            System.Windows.MessageBox.Show(Localizer.Get("Msg_FolderNotExist"), Localizer.Get("Title_Validation"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        if (_config.MaxRamGb < _config.MinRamGb)
        {
            System.Windows.MessageBox.Show(Localizer.Get("Msg_RamMaxMin"),
                Localizer.Get("Title_Validation"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RestoreSnapshot();
        DialogResult = false;
        Close();
    }

    private void RestoreSnapshot()
    {
        var original = JsonSerializer.Deserialize<ServerConfig>(_snapshot);
        if (original is null) return;

        _config.Name = original.Name;
        _config.FolderPath = original.FolderPath;
        _config.JarFile = original.JarFile;
        _config.JavaPath = original.JavaPath;
        _config.MinRamGb = original.MinRamGb;
        _config.MaxRamGb = original.MaxRamGb;
        _config.ExtraJvmArgs = original.ExtraJvmArgs;
        _config.PlayitEnabled = original.PlayitEnabled;
    }
}
