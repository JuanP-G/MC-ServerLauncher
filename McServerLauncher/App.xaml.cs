using System.Globalization;
using System.Windows;
using McServerLauncher.Services;

namespace McServerLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply the saved language BEFORE creating the window.
        var lang = new AppSettingsService().Load().Language;
        if (!string.IsNullOrWhiteSpace(lang))
        {
            try
            {
                var ci = new CultureInfo(lang);
                CultureInfo.CurrentUICulture = ci;
                CultureInfo.DefaultThreadCurrentUICulture = ci;
            }
            catch
            {
                // Invalid language code: fall back to the system one.
            }
        }

        base.OnStartup(e);
    }
}
