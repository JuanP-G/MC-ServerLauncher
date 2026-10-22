using System.ComponentModel;
using Avalonia.Controls;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;
using McServerLauncher.Views;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// The memory mode on the Performance page, and the notice a server moved to Automatic shows.
/// </summary>
/// <remarks>
/// The mode is nullable, and the page shows a null as Automatic, which is how such a server runs. The
/// trap is writing that into the draft just by opening the page: the save bar would be up before
/// anybody touched anything, and saving would quietly turn "never chose" into a choice and take the
/// notice away. These run with real controls, as the other settings tests explain.
/// </remarks>
[Collection("avalonia")]
public class MemorySettingsTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-memory-" + Guid.NewGuid()
        .ToString("N"));

    private ServerConfig Server(MemoryMode? mode = null, string extra = "")
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "server.properties"), "max-players=8\n");
        return new ServerConfig { Name = "mem", FolderPath = folder, MemoryMode = mode, MinRamGb = 2, MaxRamGb = 6, ExtraJvmArgs = extra };
    }

    private static ServerSettingsView Open(ServerConfig config)
    {
        var view = new ServerSettingsView(new ServerViewModel(config), ServerSettingsPage.Performance);
        AvaloniaFixture.Pump();
        return view;
    }

    private static T Named<T>(ServerSettingsView view, string name) where T : Control =>
        view.Section(ServerSettingsPage.Performance).FindControl<T>(name)
            ?? throw new InvalidOperationException($"no hay un control llamado {name}");

    [Fact]
    public void AServerThatNeverChoseShowsAutomaticWithoutBecomingAChoice() =>
        ui.Run(() =>
        {
            var config = Server();
            var view = Open(config);

            Assert.True(Named<RadioButton>(view, "AutoMemory").IsChecked);
            Assert.False(Named<WrapPanel>(view, "ManualBoxes").IsVisible);
            Assert.True(Named<TextBlock>(view, "AutoDesc").IsVisible);
            Assert.False(view.IsDirty);

            Assert.True(view.Save());
            Assert.Null(config.MemoryMode);
        });

    [Fact]
    public void PickingManualShowsTheBoxesAndIsSaved() =>
        ui.Run(() =>
        {
            var config = Server();
            var view = Open(config);

            Named<RadioButton>(view, "ManualMemory").IsChecked = true;
            AvaloniaFixture.Pump();

            Assert.True(Named<WrapPanel>(view, "ManualBoxes").IsVisible);
            Assert.False(Named<TextBlock>(view, "AutoDesc").IsVisible);
            Assert.True(view.IsDirty);

            Assert.True(view.Save());
            Assert.Equal(MemoryMode.Manual, config.MemoryMode);
        });

    [Fact]
    public void InAutomaticAMaxBelowTheMinIsNotAnError() =>
        ui.Run(() =>
        {
            // The two numbers wait, unused, for a way back; they are checked when they matter.
            var config = Server(MemoryMode.Automatic);
            config.MinRamGb = 8;
            config.MaxRamGb = 4;
            var view = Open(config);

            Named<RadioButton>(view, "AutoMemory").IsChecked = true;
            AvaloniaFixture.Pump();

            Assert.True(view.Save());
        });

    [Fact]
    public void ArgumentsThatSizeTheMemoryTakeTheChoiceAway() =>
        ui.Run(() =>
        {
            var view = Open(Server(MemoryMode.Automatic, "-Xmx8G"));

            Assert.True(Named<RadioButton>(view, "ManualMemory").IsChecked);
            Assert.False(Named<RadioButton>(view, "AutoMemory").IsEnabled);
            Assert.True(Named<Border>(view, "UserFlagsWarn").IsVisible);
        });

    [Fact]
    public void TwoPagesOpenAtOnceDoNotPickForEachOther() =>
        ui.Run(() =>
        {
            // Radio buttons with one group name are one group across pages: these two used to
            // uncheck each other's buttons and answer back for ever, which froze the window.
            var first = Server(MemoryMode.Automatic);
            var second = Server(MemoryMode.Automatic);
            var one = Open(first);
            var two = Open(second);

            Named<RadioButton>(two, "ManualMemory").IsChecked = true;
            AvaloniaFixture.Pump();

            Assert.True(Named<RadioButton>(one, "AutoMemory").IsChecked);
            Assert.True(Named<RadioButton>(two, "ManualMemory").IsChecked);
            Assert.True(two.Save());
            Assert.Equal(MemoryMode.Automatic, first.MemoryMode);
            Assert.Equal(MemoryMode.Manual, second.MemoryMode);
        });

    [Fact]
    public void TheNoticeOffersTheOldNumbersAndGoesOnceAnswered() =>
        ui.Run(() =>
        {
            var config = Server();
            var vm = new ServerViewModel(config);
            var announced = new List<string?>();
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => announced.Add(e.PropertyName);

            Assert.True(vm.ShowMemoryNotice);
            Assert.Contains("2", vm.MemoryNoticeManualText);
            Assert.Contains("6", vm.MemoryNoticeManualText);

            vm.ReturnToManualMemoryCommand.Execute(null);
            AvaloniaFixture.Pump();

            Assert.Equal(MemoryMode.Manual, config.MemoryMode);
            Assert.False(vm.ShowMemoryNotice);
            Assert.Contains(nameof(ServerViewModel.ShowMemoryNotice), announced);
        });

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }
}
