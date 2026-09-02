using System.Collections;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using McServerLauncher.Models;

namespace McServerLauncher.Views;

/// <summary>
/// The console tab: the server's output, the category filters and the command box.
/// </summary>
/// <remarks>
/// Split out of <see cref="MainWindow"/>, which was 717 lines of XAML with every region of the app
/// in it. Copying console lines to the clipboard is console behaviour and had no business living in
/// the window's code-behind; now it sits next to the list it reads from.
/// </remarks>
public partial class ServerConsoleView : UserControl
{
    public ServerConsoleView() => InitializeComponent();

    private void ConsoleCopy_Click(object? sender, RoutedEventArgs e) => _ = CopyConsole(selectedOnly: true);

    private void ConsoleCopyAll_Click(object? sender, RoutedEventArgs e) => _ = CopyConsole(selectedOnly: false);

    private void ConsoleList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopyConsole(selectedOnly: true);
            e.Handled = true;
        }
    }

    private async System.Threading.Tasks.Task CopyConsole(bool selectedOnly)
    {
        IList source = selectedOnly && ConsoleList.SelectedItems is { Count: > 0 }
            ? ConsoleList.SelectedItems
            : ConsoleList.Items;

        // Named explicitly rather than relying on ToString(). A console line is a record now, and a
        // record's generated ToString prints "ConsoleLine { Text = …, Kind = … }" — which would have
        // compiled cleanly and quietly filled the clipboard with that instead of the log. The record
        // overrides ToString for exactly this reason; saying so here means the two can never disagree.
        var lines = source.Cast<object?>()
            .Select(o => o is ConsoleLine line ? line.Text : o?.ToString() ?? string.Empty);
        var text = string.Join(Environment.NewLine, lines);

        // The clipboard belongs to the window, not to this control: a UserControl has no clipboard
        // of its own, so it is reached through the top level this view happens to be inside.
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (!string.IsNullOrEmpty(text) && clipboard is not null)
        {
            try { await clipboard.SetTextAsync(text); } catch { /* clipboard busy */ }
        }
    }
}
