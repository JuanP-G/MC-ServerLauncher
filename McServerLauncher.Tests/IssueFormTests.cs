using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The "Report a bug" button opens the bug form with the version and the system already in it, so
/// the address has to name a form that exists and fields the form actually has.
/// </summary>
/// <remarks>
/// GitHub fills a form field from the query string by the field's <c>id</c> and ignores a name it
/// does not know, without a word. Rename a form file, or a field, and the button keeps opening a
/// page that looks fine and has nothing filled in; nothing would fail. These tests are the failure.
/// </remarks>
public class IssueFormTests
{
    private static string FormsFolder() =>
        Path.Combine(LocalizationTests.RepoRoot(), ".github", "ISSUE_TEMPLATE");

    [Theory]
    [InlineData("es", AppLinks.BugFormSpanish)]
    [InlineData("ES", AppLinks.BugFormSpanish)]
    [InlineData("en", AppLinks.BugFormEnglish)]
    [InlineData("de", AppLinks.BugFormEnglish)]
    [InlineData("fr", AppLinks.BugFormEnglish)]
    [InlineData("pt", AppLinks.BugFormEnglish)]
    public void TheButtonPicksTheFormInTheAppsLanguage(string language, string form)
    {
        var url = AppLinks.NewBugReport("1.21.6", language);

        Assert.StartsWith(AppLinks.Repository + "/issues/new?template=" + form + "&", url);
    }

    [Fact]
    public void TheAddressCarriesTheVersionAndTheSystem()
    {
        var url = AppLinks.NewBugReport("1.21.6", "es");

        Assert.Contains("&version=1.21.6", url);
        Assert.Contains("&os=" + AppLinks.OperatingSystemName(), url);
        Assert.True(BrowserLauncher.IsWebUrl(url));
    }

    [Fact]
    public void AnOddVersionCannotBreakTheAddress()
    {
        var url = AppLinks.NewBugReport("1.2.3 beta&x=1", "en");

        Assert.Contains("&version=1.2.3%20beta%26x%3D1", url);
        Assert.DoesNotContain("&x=1", url);
    }

    [Theory]
    [InlineData(AppLinks.BugFormSpanish)]
    [InlineData(AppLinks.BugFormEnglish)]
    public void TheFormsTheButtonNamesExistAndHaveTheFieldsItFills(string form)
    {
        var path = Path.Combine(FormsFolder(), form);
        Assert.True(File.Exists(path), $"{form} is not in .github/ISSUE_TEMPLATE/");

        var text = File.ReadAllText(path);
        Assert.Contains("id: version", text);
        Assert.Contains("id: os", text);
        foreach (var name in new[] { "Windows", "Linux", "macOS" })
            Assert.Contains("- " + name, text);
    }

    [Fact]
    public void TheAboutButtonIsBoundToACommandThatExists()
    {
        // The views bind with CompileBindings off: a wrong command name compiles, shows the button
        // and does nothing when it is pressed. So check both ends of the name by hand.
        var xaml = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "AboutView.axaml"));
        Assert.Contains("Command=\"{Binding ReportIssueCommand}\"", xaml);

        var command = typeof(McServerLauncher.ViewModels.MainViewModel).GetProperty("ReportIssueCommand");
        Assert.NotNull(command);
        Assert.True(typeof(System.Windows.Input.ICommand).IsAssignableFrom(command!.PropertyType));
    }

    [Fact]
    public void TheSystemNameIsOneTheFormOffers()
    {
        Assert.Contains(AppLinks.OperatingSystemName(), new[] { "Windows", "Linux", "macOS" });
    }

    [Fact]
    public void NoFormMakesAnythingButTheStoryRequired()
    {
        // The reason for the redesign: people who do not know a version or a path gave up, or left
        // the fields empty and wrote everything in the first one. One required box, the rest optional.
        foreach (var path in Directory.GetFiles(FormsFolder(), "*.yml").Where(p => !p.EndsWith("config.yml")))
        {
            var required = File.ReadAllText(path).Split('\n').Count(l => l.Trim() == "required: true");
            Assert.True(required == 1, $"{Path.GetFileName(path)} has {required} required fields; it should have exactly one");
        }
    }
}
