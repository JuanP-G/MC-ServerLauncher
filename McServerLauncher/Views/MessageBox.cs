using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using McServerLauncher.Localization;

namespace McServerLauncher.Views;

/// <summary>
/// Small cross-platform message box helper (Avalonia has no built-in one). Provides an
/// information/warning dialog and a yes/no confirmation, both modal and localized.
/// </summary>
public static class MessageBox
{
    private static Window? MainWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>Shows an OK-only dialog.</summary>
    public static Task ShowAsync(string message, string title, Window? owner = null)
        => BuildAndShow(message, title, confirm: false, owner);

    /// <summary>Shows a Yes/No dialog. Returns true if the user accepts.</summary>
    public static async Task<bool> ConfirmAsync(string message, string title, Window? owner = null)
        => await BuildAndShow(message, title, confirm: true, owner) is true;

    private static Task<bool?> BuildAndShow(string message, string title, bool confirm, Window? owner)
    {
        var result = new TaskCompletionSource<bool?>();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 360,
            MaxWidth = 560
        };

        void Close(bool? r) { result.TrySetResult(r); window.Close(); }

        if (confirm)
        {
            var no = new Button { Content = Localizer.Get("Cancel"), MinWidth = 90 };
            no.Click += (_, _) => Close(false);
            var yes = new Button { Content = Localizer.Get("Whatsnew_Ok"), MinWidth = 90, Classes = { "accent" } };
            yes.Click += (_, _) => Close(true);
            buttons.Children.Add(no);
            buttons.Children.Add(yes);
        }
        else
        {
            var ok = new Button { Content = Localizer.Get("Whatsnew_Ok"), MinWidth = 90, Classes = { "accent" } };
            ok.Click += (_, _) => Close(true);
            buttons.Children.Add(ok);
        }

        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    MaxWidth = 500
                },
                buttons
            }
        };

        var ownerWindow = owner ?? MainWindow;
        if (ownerWindow is not null)
            window.ShowDialog(ownerWindow);
        else
            window.Show();

        return result.Task;
    }
}
