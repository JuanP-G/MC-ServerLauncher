using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>
/// The card that takes the place of the game and network settings on a native server: it says they
/// live in <c>pumpkin.toml</c> and opens that file.
/// </summary>
/// <remarks>
/// Opened with whatever the system uses for the file, not edited here. The app reads a handful of
/// its values (<see cref="PumpkinConfig"/>) and understands no more of it than that; a page that
/// rewrote it would be one bug away from dropping settings it never knew were there.
/// </remarks>
public partial class NativeConfigCard : UserControl
{
    /// <summary>The server's folder. Set by the page that shows the card.</summary>
    public string Folder { get; set; } = string.Empty;

    public NativeConfigCard()
    {
        InitializeComponent();
    }

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        var path = PumpkinConfig.PathIn(Folder);

        // Pumpkin writes the file the first time it starts; a server added by hand and never
        // started may not have it yet.
        var exists = File.Exists(path);
        Missing.IsVisible = !exists;
        if (!exists) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or IOException)
        {
            // Nothing is registered to open a .toml: show where it is instead.
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true });
            }
            catch (Exception inner) when (inner is System.ComponentModel.Win32Exception
                                              or InvalidOperationException or IOException)
            {
                Missing.IsVisible = true;
            }
        }
    }
}
