using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>
/// Picking the server type as a grid of cards rather than a drop-down of bare names.
/// </summary>
/// <remarks>
/// <para>
/// The drop-down said "Paper", "Fabric", "NeoForge" and nothing else. Which of those take plugins,
/// which take mods, and which can be joined from Bedrock were all invisible — and the last one in
/// particular is not a detail: picking a mod loader and finding out weeks later that Bedrock
/// players cannot reach it costs an evening.
/// </para>
/// <para>
/// Built in code from <see cref="ServerTypeCatalog"/>, so adding a type is a row in that table and
/// nothing else. Each card is a <see cref="RadioButton"/> underneath: keyboard navigation, screen
/// readers and single-selection all come from the framework rather than being re-implemented.
/// </para>
/// </remarks>
public partial class ServerTypePicker : UserControl
{
    private readonly Dictionary<ServerType, RadioButton> _cards = new();
    private readonly string _groupName = "types-" + Guid.NewGuid().ToString("N");

    /// <summary>Raised when the user picks a different type.</summary>
    public event EventHandler? SelectionChanged;

    public ServerTypePicker()
    {
        InitializeComponent();
        Build(ServerTypeCatalog.All);
    }

    /// <summary>The type currently picked. Setting it moves the selection without raising the event.</summary>
    /// <remarks>
    /// Tracked in a field rather than read back from whichever card is checked. Reading the cards
    /// made this one selection behind: a RadioButton raises its own "checked" before its siblings
    /// are cleared, so during the event both the old and the new card were checked and the answer
    /// came from whichever happened to come first. Everything that reacts to the change — the
    /// crossplay options, the path warning, the conversion warning — was reading the type the user
    /// had just moved away from, which looks exactly like the change not taking effect.
    /// </remarks>
    public ServerType SelectedType
    {
        get => _selected;
        set
        {
            if (!_cards.TryGetValue(value, out var card)) return;
            _suppress = true;
            _selected = value;
            card.IsChecked = true;
            _suppress = false;
        }
    }

    private ServerType _selected;
    private bool _suppress;

    /// <summary>
    /// Limits the picker to a subset, for the dialog that changes an existing server's type.
    /// </summary>
    public void Restrict(IEnumerable<ServerType> types)
    {
        var allowed = new HashSet<ServerType>(types);
        Build(ServerTypeCatalog.All.Where(e => allowed.Contains(e.Type)));
    }

    private void Build(IEnumerable<ServerTypeCatalog.Entry> entries)
    {
        CardsPanel.Children.Clear();
        _cards.Clear();

        foreach (var entry in entries)
        {
            var card = MakeCard(entry);
            _cards[entry.Type] = card;
            CardsPanel.Children.Add(card);
        }

        if (_cards.Count > 0 && _cards.Values.All(c => c.IsChecked != true))
            SelectedType = _cards.Keys.First();
    }

    private RadioButton MakeCard(ServerTypeCatalog.Entry entry)
    {
        var accent = new ImmutableSolidColorBrush(Color.Parse(entry.BadgeColor));

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (entry.Family != ServerFamily.None)
            badges.Children.Add(Badge(
                ServerTypeCatalog.FamilyEmoji(entry.Family) + " " + ServerTypeCatalog.FamilyLabel(entry.Family),
                accent));

        // The badge this whole picker exists for: which types a phone or console can actually reach.
        // Two badges, not one, because "it works" and "it connects and then the mods decide" are
        // different promises and only one of them is safe to make in blue.
        if (entry.Crossplay != CrossplayLevel.None)
        {
            var full = entry.Crossplay == CrossplayLevel.Full;
            badges.Children.Add(Badge(
                Localizer.Get(full ? "Badge_Bedrock" : "Badge_BedrockPartial"),
                new ImmutableSolidColorBrush(Color.Parse(full ? "#3E8AC0" : "#B07A2B"))));
        }

        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14
        });
        content.Children.Add(new TextBlock
        {
            Text = Localizer.Get(entry.DescriptionKey),
            FontSize = 11,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 150
        });
        if (badges.Children.Count > 0) content.Children.Add(badges);

        var card = new RadioButton
        {
            GroupName = _groupName,
            Content = content,
            // The enum value itself, never its name as a string. Nothing in the app reads it back
            // — the picker keeps its own dictionary — but it is how the tests identify a card,
            // and it is the obvious place to look for a card's identity when debugging.
            Tag = entry.Type,
            Theme = (ControlTheme?)Resources["TypeCardTheme"],
            Width = 176,
            // One height for every card, so the rows line up. Left to fit their content they came
            // out at three different heights and the grid read as broken rather than compact.
            Height = 104,
            Margin = new Avalonia.Thickness(0, 0, 8, 8),
            Padding = new Avalonia.Thickness(11, 9),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        // The card has room for a badge, not for the caveat behind it. The tooltip carries the rest.
        var tip = Localizer.Get(entry.DescriptionKey);
        if (entry.Crossplay == CrossplayLevel.Partial)
            tip += "\n\n" + Localizer.Get("Crossplay_PartialNote");
        ToolTip.SetTip(card, tip);

        Paint(card, accent, picked: false);
        card.IsCheckedChanged += (_, _) =>
        {
            Paint(card, accent, picked: card.IsChecked == true);
            if (card.IsChecked != true) return;

            // Before the event, so every handler sees the type that was just picked.
            _selected = entry.Type;
            if (!_suppress) SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        return card;
    }

    /// <summary>
    /// Colours a card for its state, in its own type's accent rather than one shared highlight.
    /// </summary>
    /// <remarks>
    /// Deliberately grey and translucent rather than named theme colours: the same two values have
    /// to stay legible on a light and a dark background, and a fixed pair of neutrals does that
    /// without a resource lookup that could be missing in either theme.
    /// </remarks>
    private static void Paint(RadioButton card, IBrush accent, bool picked)
    {
        card.BorderThickness = new Avalonia.Thickness(picked ? 2 : 1);
        card.BorderBrush = picked ? accent : new ImmutableSolidColorBrush(Color.Parse("#40808080"));
        card.Background = picked
            ? new ImmutableSolidColorBrush(Color.Parse("#22808080"))
            : Brushes.Transparent;
    }

    private static Border Badge(string text, IBrush accent) => new()
    {
        Background = accent,
        CornerRadius = new Avalonia.CornerRadius(3),
        Padding = new Avalonia.Thickness(5, 1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        }
    };
}
