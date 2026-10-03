using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using System.Text.RegularExpressions;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using static McServerLauncher.Services.PlayitApiService;

namespace McServerLauncher.Tests;

/// <summary>What a server card shows after the account is asked about its tunnels.</summary>
public class TunnelAddressSyncTests
{
    private static PlayitTunnel Tcp(int port, string host) => new("t" + port, "x", port, host, null, "tcp", 30000);
    private static PlayitTunnel Udp(int port, string host, int pub = 51917) => new("u" + port, "x", port, host, null, "udp", pub);

    [Fact]
    public void WhenTheAccountCannotBeAskedTheShownAddressStays() =>
        Assert.Equal("old.ply.gg", TunnelAddressSync.JavaAddress("old.ply.gg", tunnels: null, 25565));

    [Fact]
    public void WhenTheAccountSaysThereIsNoTunnelTheAddressGoes() =>
        Assert.Equal("", TunnelAddressSync.JavaAddress("old.ply.gg", [], 25565));

    [Fact]
    public void ABedrockTunnelOnTheJavaPortIsNotTheJavaAddress() =>
        Assert.Equal("", TunnelAddressSync.JavaAddress("old", [Udp(25565, "udp.ply.gg")], 25565));

    [Fact]
    public void TheJavaTunnelOnThePortIsShown() =>
        Assert.Equal("java.ply.gg", TunnelAddressSync.JavaAddress("old", [Tcp(25565, "java.ply.gg")], 25565));

    [Fact]
    public void AfterTheBedrockPortMovedTheOldAddressIsNotKept()
    {
        // The reported case: moved from 19132 to 19134, and the card went on showing 19132's tunnel,
        // which belonged to another server.
        var found = TunnelAddressSync.Bedrock([Udp(19132, "irvine-serenity.tun.ply.gg")], 19134);

        Assert.Equal(("", "", BedrockAddressState.LocalOnly), found);
    }

    [Fact]
    public void TheBedrockTunnelOnTheNewPortIsFound() =>
        Assert.Equal(("new.ply.gg", "40000", BedrockAddressState.Ready),
            TunnelAddressSync.Bedrock([Udp(19134, "new.ply.gg", 40000)], 19134));

    [Fact]
    public void ABedrockTunnelWithoutItsAddressYetIsWaiting() =>
        Assert.Equal(BedrockAddressState.Waiting, TunnelAddressSync.Bedrock([Udp(19134, "", 0)], 19134)!.Value.State);

    [Fact]
    public void WhenTheAccountCannotBeAskedThereIsNoBedrockAnswer() =>
        Assert.Null(TunnelAddressSync.Bedrock(null, 19134));
}

/// <summary>The server list's header and rows bind to names that exist.</summary>
public class ServerListBindingTests
{
    private static string MainWindow() =>
        File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml"));

    [Theory]
    [InlineData("ShowNewServerCommand")]
    [InlineData("EditServerCommand")]
    [InlineData("RemoveServerCommand")]
    [InlineData("ShowServerDetail")]
    [InlineData("ShowEmptyState")]
    [InlineData("NewServerPanel")]
    [InlineData("IsCreatingServer")]
    [InlineData("HasNewServerDraft")]
    [InlineData("NewServerTip")]
    public void TheListAndTheDetailUseNamesTheMainViewModelHas(string name)
    {
        Assert.Contains(name, MainWindow());
        Assert.NotNull(typeof(MainViewModel).GetProperty(name));
    }

    [Fact]
    public void TheNewServerButtonRemindsAndKeepsQuietWhileThePanelIsOpen()
    {
        var xaml = Regex.Replace(MainWindow(), @"\s+", " ");

        Assert.Matches(@"<Panel [^>]*Classes=""newserver""[^>]*Classes\.quiet=""\{Binding IsCreatingServer\}""[^>]*b:BeaconBehavior\.IsEnabled=""True""", xaml);
    }

    [Fact]
    public void TheOldFooterButtonsAreGone()
    {
        var xaml = MainWindow();

        Assert.DoesNotContain("AddServerCommand", xaml);
        Assert.DoesNotContain("CreateServerCommand", xaml);
    }

    [Fact]
    public void TheRowActionsActOnTheirOwnRow()
    {
        var xaml = MainWindow();

        Assert.Matches(@"EditServerCommand[^>]*\s+CommandParameter=""\{Binding\}""", Regex.Replace(xaml, @"\s+", " "));
        Assert.Matches(@"RemoveServerCommand[^>]*\s+CommandParameter=""\{Binding\}""", Regex.Replace(xaml, @"\s+", " "));
    }
}

/// <summary>All the motion lives in one file.</summary>
public class MotionTests
{
    [Fact]
    public void NoViewDeclaresMotionOfItsOwn()
    {
        // One place to review it, and one place to tone it down.
        var root = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher");
        var offenders = Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => Path.GetFileName(f) != "Motion.axaml")
            .Where(f =>
            {
                var xaml = File.ReadAllText(f);
                return xaml.Contains("<Transitions>") || xaml.Contains("<Animation ") || xaml.Contains("<Animation>");
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheMotionStylesAreLoadedByTheApp() =>
        Assert.Contains("/Styles/Motion.axaml",
            File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "App.axaml")));

    [Fact]
    public void NoKeyframeDependsOnTheLanguageOfTheSystem()
    {
        // Avalonia parses a cue and a key spline with the system's culture. In Spanish (decimal
        // comma) "96.4%" and "0.45,0,0.25,1" are both invalid, and the animation simply stays on its
        // first frame with nothing said: the "+ Nuevo" reminder never played on this machine.
        var root = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher");
        var risky = new Regex(@"Cue=""\d+[.,]\d+%""|KeySpline=", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, i)))
            .Where(x => risky.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

        Assert.Empty(offenders);
    }
}

/// <summary>The "+ Nuevo" reminder: when it plays, and when it keeps quiet.</summary>
[Collection("avalonia")]
public class BeaconTests(AvaloniaFixture ui)
{
    private static Panel Beacon()
    {
        var panel = new Panel { Width = 40, Height = 20 };
        panel.Classes.Add("newserver");
        McServerLauncher.Behaviors.BeaconBehavior.SetIsEnabled(panel, true);
        return panel;
    }

    [Fact]
    public void EachReminderPlaysAgainByTakingTurnsWithTwoClasses() =>
        ui.Run(() =>
        {
            // One class would only animate the first time: a style animation starts when its
            // selector starts to match, and a class that is already there matches already.
            var panel = Beacon();
            var window = new Window { Content = panel };
            window.Show();

            McServerLauncher.Behaviors.BeaconBehavior.Fire(panel);
            Assert.Contains("beacon-a", panel.Classes);

            McServerLauncher.Behaviors.BeaconBehavior.Fire(panel);
            Assert.Contains("beacon-b", panel.Classes);
            Assert.DoesNotContain("beacon-a", panel.Classes);

            McServerLauncher.Behaviors.BeaconBehavior.Fire(panel);
            Assert.Contains("beacon-a", panel.Classes);
            window.Close();
        });

    [Fact]
    public void WhileWhatItPointsToIsOpenItKeepsQuiet() =>
        ui.Run(() =>
        {
            var panel = Beacon();
            var window = new Window { Content = panel };
            window.Show();
            panel.Classes.Add("quiet");

            McServerLauncher.Behaviors.BeaconBehavior.Fire(panel);

            Assert.DoesNotContain("beacon-a", panel.Classes);
            Assert.DoesNotContain("beacon-b", panel.Classes);
            window.Close();
        });

    [Fact]
    public void TheAnimationsItTakesTurnsWithExist()
    {
        var motion = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Styles", "Motion.axaml"));

        foreach (var cls in new[] { "beacon-a", "beacon-b" })
        foreach (var part in new[] { "shine", "halo" })
            Assert.Contains($"Panel.newserver.{cls}:not(:pointerover) Border.{part}", motion);
    }
}

/// <summary>The one underline of the server tabs, which slides to the picked tab.</summary>
[Collection("avalonia")]
public class TabUnderlineTests(AvaloniaFixture ui)
{
    private static (Window Window, TabControl Tabs, McServerLauncher.Controls.TabUnderline Line, ListBox Inner) Build(object? thirdHeader = null)
    {
        var inner = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Consola", Content = inner },
                new TabItem { Header = "Jugadores", Content = new TextBlock { Text = "2" } },
                new TabItem { Header = thirdHeader ?? "Copias", Content = new TextBlock { Text = "3" } },
            }
        };
        var line = new McServerLauncher.Controls.TabUnderline { Tabs = tabs };
        var window = new Window { Width = 800, Height = 400, Content = new Panel { Children = { tabs, line } } };
        window.Show();
        Settle(window);
        return (window, tabs, line, inner);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++) { AvaloniaFixture.Pump(); window.UpdateLayout(); }
    }

    private static TextBlock TitleOf(TabControl tabs, int index) =>
        ((TabItem)tabs.ContainerFromIndex(index)!).GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Opacity > 0);

    [Fact]
    public void TheLineSitsUnderThePickedTabsTitleAndFollowsIt() =>
        ui.Run(() =>
        {
            var (window, tabs, line, _) = Build();

            foreach (var index in new[] { 0, 1, 2, 0 })
            {
                tabs.SelectedIndex = index;
                Settle(window);
                var title = TitleOf(tabs, index);
                var at = title.TranslatePoint(default, line)!.Value;

                Assert.Equal(at.X, line.Target.X, 1);
                Assert.Equal(title.Bounds.Width, line.Target.Width, 1);
            }
            window.Close();
        });

    [Fact]
    public void TheThemesOwnLinePerTabIsHidden() =>
        ui.Run(() =>
        {
            var (window, tabs, _, _) = Build();

            Assert.DoesNotContain(tabs.GetVisualDescendants().OfType<Border>(),
                b => b.Name == "PART_SelectedPipe" && b.IsEffectivelyVisible);
            window.Close();
        });

    [Fact]
    public void AListChangingItsSelectionInsideAPageDoesNotCountAsChangingTab() =>
        ui.Run(() =>
        {
            // Selection events bubble: the console list picking a line arrives at the tab strip too.
            var (window, tabs, line, inner) = Build();
            var host = tabs.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .First(p => p.Name == "PART_SelectedContentHost");
            var before = line.Target;
            var classes = string.Join(",", host.Classes);

            inner.SelectedIndex = 2;
            Settle(window);

            Assert.Equal(before, line.Target);
            Assert.Equal(classes, string.Join(",", host.Classes));
            window.Close();
        });

    [Fact]
    public void ATitleThatHoldsItsWidthIsUnderlinedByTheWordOnShow() =>
        ui.Run(() =>
        {
            // The Mods / Plugins tab keeps the width of the longer word so the tabs after it never
            // shift; the line still only goes under the word that is actually showing.
            var title = new Panel
            {
                Children =
                {
                    new TextBlock { Text = "Plugins", Opacity = 0 },
                    new TextBlock { Text = "Mods", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                }
            };
            var (window, tabs, line, _) = Build(title);

            tabs.SelectedIndex = 2;
            Settle(window);

            var shown = (TextBlock)title.Children[1];
            Assert.Equal(shown.Bounds.Width, line.Target.Width, 1);
            Assert.True(line.Target.Width < title.Bounds.Width);
            window.Close();
        });
}
