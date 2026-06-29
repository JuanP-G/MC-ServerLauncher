using System.Windows;
using McServerLauncher.Localization;
using Wpf.Ui.Controls;

namespace McServerLauncher.Views;

/// <summary>Shows the current version's what's-new notes after an update.</summary>
public partial class WhatsNewDialog : FluentWindow
{
    public WhatsNewDialog(string version)
    {
        InitializeComponent();
        HeaderText.Text = string.Format(Localizer.Get("Whatsnew_HeaderFmt"), version);
        BodyText.Text = Localizer.Get("Whatsnew_Body");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
