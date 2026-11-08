using Avalonia.Controls;
using McServerLauncher.Models;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// The Windows Firewall card on the Crossplay page, with real controls.
/// </summary>
/// <remarks>
/// The card is looked up by name from code, and a wrong name in a view fails silently, so the wiring is
/// built for real. What is read from the firewall itself is covered by
/// <see cref="WindowsFirewallServiceTests"/>; here only the cases that must never touch it.
/// </remarks>
[Collection("avalonia")]
public class FirewallCardTests(AvaloniaFixture ui)
{
    private static CrossplaySection Open(bool crossplay)
    {
        var config = new ServerConfig
        {
            Name = "ServerMC",
            FolderPath = Path.GetTempPath(),
            Type = ServerType.Paper,
            CrossplayEnabled = crossplay
        };
        var section = new CrossplaySection(new ServerSettingsDraft(config));
        AvaloniaFixture.Pump();
        return section;
    }

    [Fact]
    public void TheCardIsHiddenWhileCrossplayIsOff()
    {
        // No Bedrock port, so nothing to allow; and no reason to read a thousand firewall rules.
        ui.Run(() =>
        {
            var section = Open(crossplay: false);

            var card = section.FindControl<Border>("FirewallCard");
            Assert.NotNull(card);
            Assert.False(card.IsVisible);
        });
    }

    [Fact]
    public void TheCardHasEveryControlTheCodeLooksFor()
    {
        ui.Run(() =>
        {
            var section = Open(crossplay: false);

            foreach (var name in new[] { "FirewallStatus", "FirewallHint", "FirewallNote" })
                Assert.NotNull(section.FindControl<TextBlock>(name));
            Assert.NotNull(section.FindControl<Button>("FirewallButton"));
        });
    }
}
