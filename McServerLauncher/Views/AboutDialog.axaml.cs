using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>
/// Version, where the code lives, where to report a bug, and the notices we are obliged to show.
/// </summary>
/// <remarks>
/// The app could always tell you a new version existed and never that you were on the newest one:
/// <c>UpdateService</c> only surfaced through the banner, which by definition appears when there is
/// something to install. "Nothing to update" is an answer people come looking for, and until now
/// there was nowhere to find it.
/// </remarks>
public partial class AboutDialog : Window
{
    /// <summary>Where the source, the issues and the releases live.</summary>
    /// <remarks>
    /// Written out rather than read from the git remote: a clone with a renamed remote, or a build
    /// from a zip with no <c>.git</c> at all, would otherwise send people nowhere.
    /// </remarks>
    private const string Repo = "https://github.com/JuanP-G/MC-ServerLauncher";

    /// <summary>What the app is standing on. Named, because they earned it.</summary>
    private static readonly string[] Credits =
    {
        "Avalonia", ".NET 9", "CommunityToolkit.Mvvm", "FluentIcons",
        "Adoptium Temurin", "Modrinth", "PaperMC", "Purpur",
        "Fabric", "Forge", "NeoForge", "GeyserMC", "Floodgate", "Playit.gg",
    };

    // Parameterless constructor for the Avalonia XAML loader / designer only. A default parameter
    // value does not satisfy it: it looks for a genuine zero-argument constructor.
    public AboutDialog() : this(false) { }

    /// <param name="updateAvailable">
    /// What the app already learned when it checked at startup. Passed in rather than looked up:
    /// opening a dialog called "About" is not a reason to make a network request, and the answer
    /// is already known.
    /// </param>
    public AboutDialog(bool updateAvailable)
    {
        InitializeComponent();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var shown = v is null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        VersionText.Text = string.Format(Localizer.Get("About_VersionFmt"), shown);

        CreditsList.ItemsSource = Credits;

        // The app could always tell you a new version existed and never that there wasn't one:
        // the banner only appears when there is something to install. "You're up to date" is an
        // answer people come looking for, and this is the first place that gives it.
        UpdateDot.Fill = new SolidColorBrush(Color.Parse(updateAvailable ? "#E3A82B" : "#3FB950"));
        UpdateText.Text = Localizer.Get(updateAvailable ? "About_Newer" : "About_UpToDate");
    }

    private void Repo_Click(object? sender, RoutedEventArgs e) => BrowserLauncher.Open(Repo);

    private void Bug_Click(object? sender, RoutedEventArgs e) =>
        BrowserLauncher.Open(Repo + "/issues/new");

    private void Releases_Click(object? sender, RoutedEventArgs e) =>
        BrowserLauncher.Open(Repo + "/releases/latest");

    /// <summary>Shows the notes for the version that is running, not only after an update.</summary>
    private void Whatsnew_Click(object? sender, RoutedEventArgs e)
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        var current = new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        // NotesSince is exclusive of lastSeen, so asking from the version just below this one is
        // what returns this version's own notes rather than an empty list.
        var previous = current.Build > 0
            ? new Version(current.Major, current.Minor, current.Build - 1)
            : new Version(current.Major, Math.Max(0, current.Minor - 1), 0);

        var sections = Changelog.NotesSince(previous, current);
        if (sections.Count == 0) return;

        new WhatsNewDialog(current.ToString(), sections).ShowDialog(this);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
