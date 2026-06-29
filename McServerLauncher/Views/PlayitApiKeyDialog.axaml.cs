using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Localization;

namespace McServerLauncher.Views;

public partial class PlayitApiKeyDialog : Window
{
    /// <summary>The entered key (valid if the dialog returned true).</summary>
    public string ApiKey { get; private set; } = string.Empty;

    public PlayitApiKeyDialog(string? current = null)
    {
        InitializeComponent();
        KeyBox.Text = current ?? string.Empty;
    }

    private void OpenPlayit_Click(object? sender, RoutedEventArgs e)
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
            // Ignore if there's no browser available.
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            await MessageBox.ShowAsync(Localizer.Get("Msg_PasteKey"), Localizer.Get("Pk_Title"), this);
            return;
        }
        ApiKey = key;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
