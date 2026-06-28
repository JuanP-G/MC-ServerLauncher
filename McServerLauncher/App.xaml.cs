using System.Globalization;
using System.Windows;
using McServerLauncher.Services;

namespace McServerLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Aplicar el idioma guardado ANTES de crear la ventana.
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
                // Código de idioma inválido: se usa el del sistema.
            }
        }

        base.OnStartup(e);
    }
}
