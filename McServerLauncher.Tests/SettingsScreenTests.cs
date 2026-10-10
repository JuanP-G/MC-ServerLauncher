using Avalonia.Controls;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using McServerLauncher.Views;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>The settings screen saving as it goes.</summary>
public class SettingsScreenTests
{
    private static (SettingsViewModel Vm, AppSettings Settings, List<AppSettings> Saves) Make()
    {
        var settings = new AppSettings();
        var saves = new List<AppSettings>();
        var vm = new SettingsViewModel(null, settings, new AppSettingsService(), () => { }, () => { })
        {
            SaveOverride = s => saves.Add(s),
        };
        return (vm, settings, saves);
    }

    [Fact]
    public void FlippingASwitchSavesStraightAway()
    {
        var (vm, settings, saves) = Make();

        vm.CloseToTray = !settings.CloseToTray;

        Assert.Single(saves);
        Assert.Equal(vm.CloseToTray, settings.CloseToTray);
    }

    [Fact]
    public void AColourHalfTypedIsNotSaved()
    {
        // "#E0" is what the box holds half way through typing "#E05561".
        var (vm, settings, saves) = Make();
        var before = settings.Notifications.ColorError;

        vm.ColorError = "#E0";

        Assert.Empty(saves);
        Assert.Equal(before, settings.Notifications.ColorError);

        vm.ColorError = "#112233";

        Assert.Single(saves);
        Assert.Equal("#112233", settings.Notifications.ColorError);
    }

    [Fact]
    public void ConsoleColoursAreAppliedOnlyWhenValid()
    {
        var settings = new AppSettings();
        var applied = 0;
        var vm = new SettingsViewModel(null, settings, new AppSettingsService(), () => applied++, () => { })
        {
            SaveOverride = _ => { },
        };

        vm.ConsoleChatColor = "nope";
        vm.ConsoleChatColor = "#123456";

        Assert.Equal(1, applied);
        Assert.Equal("#123456", settings.ConsoleChatColor);
    }

    [Fact]
    public void ResettingPutsBackTheShippedColours()
    {
        var (vm, settings, _) = Make();
        vm.ColorInfo = "#000000";
        vm.ConsolePlayersColor = "#000000";

        vm.ResetColorsCommand.Execute(null);

        Assert.Equal(NotificationPalette.DefaultInfo, settings.Notifications.ColorInfo);
        Assert.Equal(ConsoleColors.DefaultPlayers, settings.ConsolePlayersColor);
    }

    [Fact]
    public void ThePagesAreExclusive()
    {
        var (vm, _, _) = Make();

        vm.ShowColorsCommand.Execute(null);

        Assert.True(vm.IsColorsPage);
        Assert.False(vm.IsGeneralPage);
        Assert.False(vm.IsNotificationsPage);
    }
}

/// <summary>The Bedrock port in a server's configuration.</summary>
[Collection("avalonia")]
public class BedrockPortSettingTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-bport-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private ServerConfig Server(bool crossplay)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "server.properties"), "server-port=25565\n");
        return new ServerConfig
        {
            Name = "Paper", FolderPath = _folder, Type = ServerType.Paper, CrossplayEnabled = crossplay, BedrockPort = 19132,
        };
    }

    private static (ServerSettingsView View, Control Section) Open(ServerConfig config, Func<int, string?>? owner = null)
    {
        var view = new ServerSettingsView(new ServerViewModel(config), ServerSettingsPage.Network, owner);
        return (view, view.Section(ServerSettingsPage.Network));
    }

    [Fact]
    public void WithoutCrossplayThereIsNoBedrockPortToChange() =>
        ui.Run(() =>
        {
            var (_, section) = Open(Server(crossplay: false));
            Assert.False(section.FindControl<Border>("BedrockPortCard")!.IsVisible);
        });

    [Fact]
    public void APortAnotherServerUsesIsRefusedByName() =>
        ui.Run(() =>
        {
            var config = Server(crossplay: true);
            var (view, section) = Open(config, port => port == 19140 ? "Java+Bedrock" : null);
            section.FindControl<NumericUpDown>("BedrockPortBox")!.Value = 19140;

            Assert.False(view.Save());
            Assert.Equal(19132, config.BedrockPort);
            var warning = section.FindControl<TextBlock>("BedrockPortWarning")!;
            Assert.True(warning.IsVisible);
            Assert.Contains("Java+Bedrock", warning.Text);
        });

    [Fact]
    public void AFreePortIsSavedAndWrittenToGeyser() =>
        ui.Run(() =>
        {
            var config = Server(crossplay: true);
            var (view, section) = Open(config, _ => null);
            ServerSettingsSaved? saved = null;
            view.Saved += s => saved = s;
            section.FindControl<NumericUpDown>("BedrockPortBox")!.Value = 19141;

            Assert.True(view.Save());
            Assert.Equal(19141, config.BedrockPort);
            Assert.True(saved!.BedrockPortChanged);

            var geyser = GeyserConfigService.ConfigPath(_folder, ServerType.Paper)!;
            Assert.Contains("19141", File.ReadAllText(geyser));
        });

    [Fact]
    public void LeavingThePortAloneChangesNothing() =>
        ui.Run(() =>
        {
            // Not even going there and back: the config keeps 0 for "the default", and typing the
            // default out must not turn that into a change.
            var config = Server(crossplay: true);
            config.BedrockPort = 0;
            var (view, section) = Open(config, _ => "anyone");
            var box = section.FindControl<NumericUpDown>("BedrockPortBox")!;

            box.Value = 19150;
            box.Value = 19132;

            Assert.False(view.IsDirty);
            Assert.Equal(0, config.BedrockPort);
        });
}
