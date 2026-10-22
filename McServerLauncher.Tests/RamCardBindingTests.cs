using System.ComponentModel;
using System.Text.RegularExpressions;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The RAM card of the main window: that what it binds exists, and that the line under the figure
/// appears and goes with the reading it shows.
/// </summary>
/// <remarks>
/// Read from the markup for the reason <see cref="ServerCardBindingTests"/> gives: the window is
/// <c>x:CompileBindings="False"</c>, so a mistyped name draws an empty card without an error.
/// </remarks>
[Collection("avalonia")]
public class RamCardBindingTests(AvaloniaFixture ui)
{
    private static string CardMarkup()
    {
        var path = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml");
        var xaml = File.ReadAllText(path);

        var label = xaml.IndexOf("Text=\"RAM\"", StringComparison.Ordinal);
        Assert.True(label >= 0, "la tarjeta de RAM ya no está en MainWindow.axaml");

        var start = xaml.LastIndexOf("<Border", label, StringComparison.Ordinal);
        var end = xaml.IndexOf("</Border>", label, StringComparison.Ordinal);
        return xaml[start..end];
    }

    [Fact]
    public void EveryBindingOnTheCardExistsOnTheServerViewModel()
    {
        var names = Regex.Matches(CardMarkup(), @"\{Binding (\w+)\}").Select(m => m.Groups[1].Value).ToList();

        Assert.Contains("RamText", names);
        Assert.Contains("RamDetailText", names);
        Assert.Contains("HasRamDetail", names);
        foreach (var name in names)
            Assert.True(typeof(ServerViewModel).GetProperty(name) is not null,
                $"ServerViewModel no tiene una propiedad '{name}', y la tarjeta de RAM la enlaza");
    }

    [Fact]
    public void TheLineUnderTheFigureAppearsWithItsText() =>
        ui.Run(() =>
        {
            var vm = new ServerViewModel(new ServerConfig { Name = "t", FolderPath = Path.GetTempPath() });
            var announced = new List<string?>();
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => announced.Add(e.PropertyName);

            Assert.False(vm.HasRamDetail);

            vm.RamDetailText = "Java: 157 MB de 232 MB";

            Assert.True(vm.HasRamDetail);
            Assert.Contains(nameof(ServerViewModel.HasRamDetail), announced);

            vm.RamDetailText = "";
            Assert.False(vm.HasRamDetail);
        });
}
