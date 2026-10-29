using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>
/// Small cross-platform message box helper (Avalonia has no built-in one). Provides an
/// information/warning dialog and a yes/no confirmation, both modal and localized.
/// </summary>
public static class MessageBox
{
    private static Window? MainWindow => AppWindows.Main;

    /// <summary>Shows an OK-only dialog.</summary>
    public static Task ShowAsync(string message, string title, Window? owner = null)
        => BuildAndShow(message, title, confirm: false, owner);

    /// <summary>Shows a Yes/No dialog. Returns true if the user accepts.</summary>
    public static async Task<bool> ConfirmAsync(string message, string title, Window? owner = null)
        => await BuildAndShow(message, title, confirm: true, owner) is true;

    /// <summary>Builds the dialog, shows it over its owner and returns what the user chose.</summary>
    /// <remarks>The result is null when the window is closed with the title-bar X.</remarks>
    private static Task<bool?> BuildAndShow(string message, string title, bool confirm, Window? owner)
    {
        var result = new TaskCompletionSource<bool?>();
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 360,
            MaxWidth = 560
        };

        // If the user closes the window with the title-bar X, resolve the task anyway
        // (as "no"); otherwise the awaiting caller would hang forever.
        window.Closed += (_, _) => result.TrySetResult(null);

        void Close(bool? r) { result.TrySetResult(r); window.Close(); }
        window.Content = BuildContent(message, BuildButtons(confirm, Close));

        var ownerWindow = owner ?? MainWindow;
        if (ownerWindow is not null)
            window.ShowDialog(ownerWindow);
        else
            window.Show();

        return result.Task;
    }

    /// <summary>The message above its buttons.</summary>
    private static StackPanel BuildContent(string message, StackPanel buttons) => new()
    {
        Margin = new Avalonia.Thickness(20),
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14, MaxWidth = 500 },
            buttons
        }
    };

    /// <summary>Yes and No for a question, a single OK for a notice. Each calls <paramref name="close"/>.</summary>
    private static StackPanel BuildButtons(bool confirm, Action<bool?> close)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };

        if (confirm)
        {
            buttons.Children.Add(BuildButton(Localizer.Get("Btn_No"), accent: false, () => close(false)));
            buttons.Children.Add(BuildButton(Localizer.Get("Btn_Yes"), accent: true, () => close(true)));
        }
        else
        {
            buttons.Children.Add(BuildButton(Localizer.Get("Whatsnew_Ok"), accent: true, () => close(true)));
        }
        return buttons;
    }

    /// <summary>One button of the dialog; the main choice takes the accent style.</summary>
    private static Button BuildButton(string text, bool accent, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        if (accent) button.Classes.Add("accent");
        button.Click += (_, _) => onClick();
        return button;
    }
}
