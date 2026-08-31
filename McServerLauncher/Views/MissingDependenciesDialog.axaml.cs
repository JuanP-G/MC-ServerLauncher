using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>One missing thing, shaped for the list in the dialog.</summary>
/// <param name="Id">The id or plugin name that is missing.</param>
/// <param name="NeededByText">A ready-made line naming the jars waiting on it.</param>
public record MissingDependencyRow(string Id, string NeededByText);

/// <summary>What the user decided about a server whose content is missing something.</summary>
public enum MissingDependenciesChoice
{
    /// <summary>Do not start. The default for anything unexpected, including closing the window.</summary>
    Cancel,

    /// <summary>Fetch what is missing, then start.</summary>
    InstallAndStart,

    /// <summary>Start as things are.</summary>
    StartAnyway
}

/// <summary>
/// Shown before starting a server whose mods or plugins are waiting on something that is not there.
/// </summary>
/// <remarks>
/// <para>
/// Three ways out, not two, and that is the whole design. Blocking outright would be wrong the day
/// the check is mistaken — a dependency satisfied by something it cannot read, a name spelled
/// differently — and a check that can trap you is a check you learn to switch off. Warning without
/// stopping is what the app already did in the Mods tab, and it demonstrably does not work: the
/// panel is on a tab nobody opens before pressing Start, so the server falls over anyway.
/// </para>
/// <para>
/// A <see cref="Window"/> rather than <c>MessageBox</c> because that helper has exactly two buttons
/// and cannot show a list, and the list is most of the value: what is missing, and which mod is
/// waiting for it.
/// </para>
/// </remarks>
public partial class MissingDependenciesDialog : Window
{
    /// <summary>
    /// What the user chose. Cancel unless they said otherwise.
    /// </summary>
    /// <remarks>
    /// Closing with the title-bar X is a decision too, and the safe reading of it is "no" — so the
    /// initial value is the one that does not start a server the user may have thought better of.
    /// </remarks>
    public MissingDependenciesChoice Choice { get; private set; } = MissingDependenciesChoice.Cancel;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public MissingDependenciesDialog()
        : this(string.Empty, new List<ContentDependencyCheck.Missing>()) { }

    public MissingDependenciesDialog(string serverName, IReadOnlyList<ContentDependencyCheck.Missing> missing)
    {
        InitializeComponent();

        MessageText.Text = string.Format(Localizer.Get("Deps_StartMessageFmt"), serverName, missing.Count);
        HintText.Text = Localizer.Get("Deps_StartHint");

        MissingList.ItemsSource = missing
            .Select(m => new MissingDependencyRow(m.Id,
                string.Format(Localizer.Get("Deps_NeededByFmt"), string.Join(", ", m.NeededBy))))
            .ToList();

    }

    private void InstallAndStart_Click(object? sender, RoutedEventArgs e) => Finish(MissingDependenciesChoice.InstallAndStart);

    private void StartAnyway_Click(object? sender, RoutedEventArgs e) => Finish(MissingDependenciesChoice.StartAnyway);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Finish(MissingDependenciesChoice.Cancel);

    private void Finish(MissingDependenciesChoice choice)
    {
        Choice = choice;
        Close(choice);
    }
}
