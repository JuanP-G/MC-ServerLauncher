using Avalonia.Controls;

namespace McServerLauncher.Views;

/// <summary>The About screen: version, updates, repository links, credits and the legal notices.</summary>
public partial class AboutView : UserControl
{
    // Product names, not translations: they are what these projects call themselves.
    private static readonly string[] BuiltOn =
    [
        "Avalonia", ".NET 9", "CommunityToolkit.Mvvm", "FluentIcons", "SkiaSharp",
        "Temurin (Adoptium)", "Modrinth", "Paper", "Purpur", "Fabric", "Forge", "NeoForge",
        "Geyser", "Floodgate", "Playit.gg",
    ];

    public AboutView()
    {
        InitializeComponent();
        Credits.ItemsSource = BuiltOn;
    }
}
