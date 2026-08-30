using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// The create-server dialog reacting to the type that was picked.
/// </summary>
/// <remarks>
/// Everything here failed at some point in one afternoon, and all of it for the same reason: the
/// options are computed from the type, and the type arrived stale. A unit test cannot see any of
/// it — the wiring between the picker and the checkboxes only exists once the controls are real.
/// </remarks>
[Collection("avalonia")]
public class CreateServerDialogTests(AvaloniaFixture ui)
{
    private static (CreateServerDialog Dialog, Dictionary<ServerType, RadioButton> Cards) Open()
    {
        var dialog = new CreateServerDialog();
        dialog.Show();
        dialog.Measure(new Avalonia.Size(640, 940));
        dialog.Arrange(new Avalonia.Rect(0, 0, 640, 940));

        var picker = dialog.GetVisualDescendants().OfType<ServerTypePicker>().Single();
        return (dialog, picker.GetVisualDescendants().OfType<RadioButton>()
            .ToDictionary(c => (ServerType)c.Tag!, c => c));
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public void TheOptionsFollowTheTypeOnTheFirstPick()
    {
        // The first pick is the one that used to do nothing: the handler read the previous type, so
        // choosing Paper left the crossplay box greyed out saying Vanilla takes no plugins.
        ui.Run(() =>
        {
            var (dialog, cards) = Open();
            var crossplay = Named<CheckBox>(dialog, "CrossplayCheck");
            var multiVersion = Named<CheckBox>(dialog, "MultiVersionCheck");
            var hydraulic = Named<CheckBox>(dialog, "HydraulicCheck");

            Assert.False(crossplay.IsEnabled);      // Vanilla, the default

            cards[ServerType.Paper].IsChecked = true;
            Assert.True(crossplay.IsEnabled);       // Geyser publishes for Paper
            Assert.True(multiVersion.IsEnabled);    // plugin family
            Assert.False(hydraulic.IsEnabled);      // Hydraulic is Fabric only

            cards[ServerType.Fabric].IsChecked = true;
            Assert.True(crossplay.IsEnabled);
            Assert.False(multiVersion.IsEnabled);   // the loader already demands a matching client
            Assert.True(hydraulic.IsEnabled);       // the one place mod content reaches Bedrock

            cards[ServerType.NeoForge].IsChecked = true;
            Assert.True(crossplay.IsEnabled);
            Assert.False(hydraulic.IsEnabled);      // no NeoForge build has shipped since Feb 2026

            cards[ServerType.Forge].IsChecked = true;
            Assert.False(crossplay.IsEnabled);      // Geyser publishes nothing for Forge
        });
    }

    [Fact]
    public void AnOptionThatBecomesUnavailableIsUnticked()
    {
        // Otherwise the config would be saved asking for something the type cannot do, and the
        // install would fail after the server was already created.
        ui.Run(() =>
        {
            var (dialog, cards) = Open();
            var hydraulic = Named<CheckBox>(dialog, "HydraulicCheck");

            cards[ServerType.Fabric].IsChecked = true;
            hydraulic.IsChecked = true;

            cards[ServerType.Paper].IsChecked = true;

            Assert.False(hydraulic.IsEnabled);
            Assert.NotEqual(true, hydraulic.IsChecked);
        });
    }

    [Fact]
    public void WindowsRulesAreWarnedAboutWhateverTheType()
    {
        // These have nothing to do with the server software: the folder either cannot be created or
        // ends up named something else. They used to be swallowed — typing "Mi:Server" produced
        // "MiServer" and said nothing about it.
        ui.Run(() =>
        {
            var (dialog, cards) = Open();
            var name = Named<TextBox>(dialog, "NameBox");
            var warning = Named<TextBlock>(dialog, "PathWarning");

            foreach (var bad in new[] { "Mi:Server", "CON", "servidor." })
            {
                name.Text = bad;
                AvaloniaFixture.Pump();
                Assert.True(warning.IsVisible, $"no avisa de \"{bad}\"");
            }

            // And a perfectly ordinary name with punctuation in it is left alone: the sweep showed
            // these run fine, and refusing them would be the app inventing problems.
            foreach (var fine in new[] { "Survival 2026", "Iberia (v2)", "server#2" })
            {
                name.Text = fine;
                AvaloniaFixture.Pump();
                Assert.False(warning.IsVisible, $"avisa de \"{fine}\" sin motivo");
            }

            // Still nothing to complain about once a type with rules of its own is picked.
            cards[ServerType.Paper].IsChecked = true;
            Assert.False(warning.IsVisible);
        });
    }

    [Fact]
    public void ThePathWarningAppearsForTheTypesThatCareAndOnlyThose()
    {
        // Paper refuses to run from a path with "+" in it; the mod loaders do not care. The warning
        // has to follow both the name being typed and the type being picked.
        ui.Run(() =>
        {
            var (dialog, cards) = Open();
            var name = Named<TextBox>(dialog, "NameBox");
            var warning = Named<TextBlock>(dialog, "PathWarning");

            name.Text = "Java+Bedrock";
            AvaloniaFixture.Pump();
            Assert.False(warning.IsVisible);         // still Vanilla

            cards[ServerType.Paper].IsChecked = true;
            Assert.True(warning.IsVisible);

            cards[ServerType.NeoForge].IsChecked = true;
            Assert.False(warning.IsVisible);

            cards[ServerType.Purpur].IsChecked = true;
            Assert.True(warning.IsVisible);

            name.Text = "Java-Bedrock";
            AvaloniaFixture.Pump();
            Assert.False(warning.IsVisible);
        });
    }
}
