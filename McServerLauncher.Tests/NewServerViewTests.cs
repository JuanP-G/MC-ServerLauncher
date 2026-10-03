using McServerLauncher.Services;
using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// The create form of the new-server panel reacting to the type that was picked.
/// </summary>
/// <remarks>
/// Everything here failed at some point in one afternoon, and all of it for the same reason: the
/// options are computed from the type, and the type arrived stale. A unit test cannot see any of
/// it — the wiring between the picker and the checkboxes only exists once the controls are real.
/// </remarks>
[Collection("avalonia")]
public class NewServerCreateFormTests(AvaloniaFixture ui)
{
    private static (Window Dialog, Dictionary<ServerType, RadioButton> Cards) Open()
    {
        // The create form now lives in the in-app new-server panel; straight to its create step,
        // as the old Create button did, hosted in a window so it can be laid out.
        var view = new NewServerView();
        view.ChooseCreate();
        AvaloniaFixture.WithoutIcons(view);
        var dialog = new Window { Content = view };
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

            // Windows only: the reserved device names, the forbidden characters and the trailing
            // dot are all Windows rules. On Linux those are ordinary folder names and warning about
            // them would be the app inventing a problem — so only the "left alone" half runs there.
            if (OperatingSystem.IsWindows())
            {
                foreach (var bad in new[] { "Mi:Server", "CON", "servidor." })
                {
                    name.Text = bad;
                    AvaloniaFixture.Pump();
                    Assert.True(warning.IsVisible, $"no avisa de \"{bad}\"");
                }
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

/// <summary>The steps of the new-server panel, and what it hands back.</summary>
[Collection("avalonia")]
public class NewServerStepsTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-new-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException(name);

    [Theory]
    [InlineData("CreateCard", "CreateButton")]
    [InlineData("AddCard", "AddButton")]
    public void OneClickOnACardIsTheChoiceAndTheStepForward(string card, string finish) =>
        ui.Run(() =>
        {
            // There was a selected state and a Next button, and the mark of the picked card vanished
            // under the very pointer that had just clicked it.
            var view = new NewServerView();

            Named<Button>(view, card).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.False(Named<Control>(view, "OriginStep").IsVisible);
            Assert.True(Named<Control>(view, "DetailsStep").IsVisible);
            Assert.True(Named<Button>(view, finish).IsVisible);
            Assert.True(Named<Button>(view, "BackButton").IsVisible);
            Assert.Null(view.FindControl<Button>("NextButton"));
        });

    [Theory]
    [InlineData(1300, true)]
    [InlineData(700, false)]
    public void TheDetailsSitInTwoColumnsWhenThereIsRoomForThem(double width, bool wide) =>
        ui.Run(() =>
        {
            // The form used to stay a 760 px strip on the left, whatever the size of the window.
            var view = new NewServerView();
            view.ChooseCreate();
            AvaloniaFixture.WithoutIcons(view);
            var window = new Window { Content = view, Width = width, Height = 800 };
            window.Show();
            AvaloniaFixture.Pump();
            window.UpdateLayout();

            Assert.Equal(wide, Named<Grid>(view, "FormPanel").Classes.Contains("wide"));
            window.Close();
        });

    [Fact]
    public void ItStartsOnTheChoice() =>
        ui.Run(() =>
        {
            var view = new NewServerView();
            Assert.True(Named<Control>(view, "OriginStep").IsVisible);
            Assert.False(Named<Control>(view, "DetailsStep").IsVisible);
            Assert.False(Named<Button>(view, "BackButton").IsVisible);
        });

    private ServerDetection AServerIn(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "server.jar"), "");
        return new ServerDetection { Type = ServerType.Paper, GameVersion = "1.21.1", JarFile = "server.jar", Jars = new[] { "server.jar" } };
    }

    [Fact]
    public void AnExistingFolderIsTakenOverUnderItsOwnName() =>
        ui.Run(() =>
        {
            var view = new NewServerView();
            view.ChooseAdd();

            view.ShowDetection(_folder, AServerIn(_folder));

            Assert.Equal(new DirectoryInfo(_folder).Name, Named<TextBox>(view, "NameBox").Text);
            var config = view.TryBuildExisting(out var error);
            Assert.Null(error);
            Assert.Equal(_folder, config!.FolderPath);
            Assert.Equal(ServerType.Paper, config.Type);
        });

    [Fact]
    public void AFolderThatDoesNotExistIsRefused() =>
        ui.Run(() =>
        {
            var view = new NewServerView();
            view.ChooseAdd();
            Named<TextBox>(view, "ExistingFolderBox").Text = Path.Combine(_folder, "missing");
            Named<TextBox>(view, "NameBox").Text = "x";

            Assert.Null(view.TryBuildExisting(out var error));
            Assert.NotNull(error);
        });

    [Fact]
    public void AFolderTheAppAlreadyHasIsRefused() =>
        ui.Run(() =>
        {
            var view = new NewServerView(null, new[] { _folder });
            view.ChooseAdd();
            view.ShowDetection(_folder, AServerIn(_folder));

            Assert.Null(view.TryBuildExisting(out var error));
            Assert.Equal(McServerLauncher.Localization.Localizer.Get("Cs_ExistingAlreadyAdded"), error);
        });
}
