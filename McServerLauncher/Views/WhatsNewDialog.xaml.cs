using System.Windows;
using McServerLauncher.Localization;
using Wpf.Ui.Controls;

namespace McServerLauncher.Views;

/// <summary>Muestra las novedades de la versión actual tras una actualización.</summary>
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
