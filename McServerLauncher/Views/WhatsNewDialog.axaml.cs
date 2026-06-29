using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Localization;

namespace McServerLauncher.Views;

/// <summary>Shows the current version's what's-new notes after an update.</summary>
public partial class WhatsNewDialog : Window
{
    public WhatsNewDialog()
    {
        InitializeComponent();
    }

    public WhatsNewDialog(string version) : this()
    {
        HeaderText.Text = string.Format(Localizer.Get("Whatsnew_HeaderFmt"), version);
        BodyText.Text = Localizer.Get("Whatsnew_Body");
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close();
}
