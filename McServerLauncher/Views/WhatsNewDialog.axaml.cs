using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>Shows what's-new notes after an update, accumulating every version the user skipped.</summary>
public partial class WhatsNewDialog : Window
{
    public WhatsNewDialog()
    {
        InitializeComponent();
    }

    public WhatsNewDialog(string version, IReadOnlyList<Changelog.Section> sections) : this()
    {
        HeaderText.Text = string.Format(Localizer.Get("Whatsnew_HeaderFmt"), version);

        // Only label each section with its version when there is more than one (a single update
        // already shows the version in the header).
        var showVersionHeaders = sections.Count > 1;
        foreach (var section in sections)
        {
            var block = new StackPanel { Spacing = 4 };
            if (showVersionHeaders)
                block.Children.Add(new TextBlock
                {
                    Text = $"v{section.Version}",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 14,
                    Opacity = 0.85
                });
            block.Children.Add(new TextBlock
            {
                Text = section.Notes,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 22,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            BodyPanel.Children.Add(block);
        }
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close();
}
