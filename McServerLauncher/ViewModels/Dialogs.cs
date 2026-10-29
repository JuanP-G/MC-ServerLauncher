using Avalonia.Controls;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The questions and notices a view model puts to the user, behind an interface so what it does
/// with the answer can be run without a window.
/// </summary>
/// <remarks>
/// View models used to call <see cref="MessageBox"/> directly, which tied every "are you sure?" to a
/// real window: the decision that follows it could only be tried by clicking. They now go through
/// <see cref="Dialogs.Current"/>, which shows the real dialog unless a test has put a stand-in there.
/// </remarks>
public interface IDialogService
{
    /// <summary>Tells the user something and waits for it to be dismissed.</summary>
    Task ShowAsync(string message, string title, Window? owner = null);

    /// <summary>Asks a yes/no question. True when the user accepts.</summary>
    Task<bool> ConfirmAsync(string message, string title, Window? owner = null);
}

/// <summary>The dialog service the app uses, replaceable for tests.</summary>
/// <remarks>
/// A single ambient instance rather than a constructor parameter: view models are created in a dozen
/// places with just their config, and the dialog is only ever one — the app's own message box.
/// </remarks>
public static class Dialogs
{
    /// <summary>The service in use. The real message box unless replaced.</summary>
    public static IDialogService Current { get; set; } = new MessageBoxDialogService();

    private sealed class MessageBoxDialogService : IDialogService
    {
        public Task ShowAsync(string message, string title, Window? owner = null) =>
            MessageBox.ShowAsync(message, title, owner);

        public Task<bool> ConfirmAsync(string message, string title, Window? owner = null) =>
            MessageBox.ConfirmAsync(message, title, owner);
    }
}
