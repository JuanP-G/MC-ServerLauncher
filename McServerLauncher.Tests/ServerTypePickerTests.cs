using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// The type picker, with real controls: the bug here shipped twice and no unit test could see it.
/// </summary>
[Collection("avalonia")]
public class ServerTypePickerTests(AvaloniaFixture ui)
{
    private static Dictionary<ServerType, RadioButton> CardsOf(ServerTypePicker picker) =>
        picker.GetVisualDescendants().OfType<RadioButton>().ToDictionary(c => (ServerType)c.Tag!, c => c);

    private static ServerTypePicker Shown()
    {
        var picker = new ServerTypePicker();
        var window = new Window { Content = picker, Width = 640, Height = 400 };
        window.Show();
        window.Measure(new Avalonia.Size(640, 400));
        window.Arrange(new Avalonia.Rect(0, 0, 640, 400));
        return picker;
    }

    [Fact]
    public void TheHandlerSeesTheTypeThatWasJustPicked()
    {
        // THE regression. A RadioButton raises its own "checked" before its siblings are cleared,
        // so for that instant two cards were checked and the picker answered with whichever came
        // first — the previous one. Every handler read the type the user had just left, which is
        // why the first change appeared to do nothing and the second worked.
        //
        // Reading SelectedType *after* the dispatcher drains hides this completely: that is exactly
        // how it was missed the first time. The check has to happen inside the event.
        ui.Run(() =>
        {
            var picker = Shown();
            var cards = CardsOf(picker);
            var seenByHandler = new List<ServerType>();
            picker.SelectionChanged += (_, _) => seenByHandler.Add(picker.SelectedType);

            foreach (var target in new[] { ServerType.Paper, ServerType.Fabric, ServerType.Purpur })
                cards[target].IsChecked = true;

            Assert.Equal(
                new[] { ServerType.Paper, ServerType.Fabric, ServerType.Purpur },
                seenByHandler);
        });
    }

    [Fact]
    public void OnlyOneCardIsCheckedAfterAPick()
    {
        ui.Run(() =>
        {
            var picker = Shown();
            var cards = CardsOf(picker);

            cards[ServerType.NeoForge].IsChecked = true;

            Assert.Equal(ServerType.NeoForge, picker.SelectedType);
            Assert.Equal(new[] { ServerType.NeoForge },
                cards.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToArray());
        });
    }

    [Fact]
    public void SettingItInCodeMovesTheSelectionWithoutRaisingTheEvent()
    {
        // The dialogs pre-select the server's current type when they open. If that counted as a
        // user pick, opening the change-type window would look like a conversion request.
        ui.Run(() =>
        {
            var picker = Shown();
            var raised = 0;
            picker.SelectionChanged += (_, _) => raised++;

            picker.SelectedType = ServerType.Purpur;

            Assert.Equal(ServerType.Purpur, picker.SelectedType);
            Assert.Equal(0, raised);
        });
    }

    [Fact]
    public void EveryTypeInTheCatalogueGetsACard()
    {
        ui.Run(() =>
        {
            var cards = CardsOf(Shown());

            foreach (var type in Enum.GetValues<ServerType>())
                Assert.True(cards.ContainsKey(type), $"{type} no tiene tarjeta");
        });
    }

    [Fact]
    public void TheCardsSayWhichTakePluginsAndWhichBedrockCanReach()
    {
        // The badges are the whole reason the drop-down was replaced; a card losing them would look
        // fine and tell the user nothing.
        ui.Run(() =>
        {
            var cards = CardsOf(Shown());

            foreach (var entry in ServerTypeCatalog.All)
            {
                var texts = cards[entry.Type].GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? "").ToList();

                Assert.Contains(entry.DisplayName, texts);

                var hasBedrock = texts.Any(t => t.Contains("Bedrock", StringComparison.Ordinal));
                Assert.Equal(entry.SupportsCrossplay, hasBedrock);

                if (entry.Family != ServerFamily.None)
                    Assert.Contains(texts, t => t.Contains(ServerTypeCatalog.FamilyLabel(entry.Family)));
            }
        });
    }
}
