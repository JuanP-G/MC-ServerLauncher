using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>What the server runs on: how much memory it gets, and which Java.</summary>
public partial class PerformanceSection : UserControl, IServerSettingsSection
{
    private readonly ServerSettingsDraft _draft;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public PerformanceSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    /// <summary>True while the code sets the radio buttons, so that is not taken for the user's choice.</summary>
    private bool _syncing;

    public PerformanceSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        _draft = draft;
        DataContext = draft.Config;
        MinRamBox.ValueChanged += (_, _) => ShowError(null);
        MaxRamBox.ValueChanged += (_, _) => ShowError(null);

        // A group of its own. Radio buttons sharing a group name are one group across every page that
        // uses it, so two of these pages alive at once (two servers' settings) unchecked each other's
        // buttons, each took that as its user's choice and checked its own back, for ever.
        AutoMemory.GroupName = ManualMemory.GroupName = "MemoryMode-" + Guid.NewGuid().ToString("N");
        AutoMemory.IsCheckedChanged += (_, _) => OnModePicked();
        ManualMemory.IsCheckedChanged += (_, _) => OnModePicked();
        // The extra arguments live on another page of the same draft and can take the choice away.
        draft.Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ServerConfig.ExtraJvmArgs) or nameof(ServerConfig.MemoryMode))
                ShowMode();
        };
        ShowMode();
    }

    /// <inheritdoc />
    public void Reload() => ShowMode();

    private void OnModePicked()
    {
        if (_syncing) return;
        var mode = ManualMemory.IsChecked == true ? MemoryMode.Manual : MemoryMode.Automatic;
        if (_draft.Config.MemoryMode != mode) _draft.Config.MemoryMode = mode;
        ShowError(null);
        ShowMode();
    }

    /// <summary>Shows the mode in effect: the radio buttons, the two boxes or what Automatic would do.</summary>
    /// <remarks>
    /// A server that never chose shows as Automatic, which is how it runs, without that being
    /// written into the draft: only a click makes it a choice, so opening the page changes nothing.
    /// </remarks>
    private void ShowMode()
    {
        var config = _draft.Config;
        var userFlags = JvmArgs.SetsMemory(config.ExtraJvmArgs);
        var effective = MemoryProfile.Effective(config);

        _syncing = true;
        AutoMemory.IsChecked = effective == MemoryMode.Automatic;
        ManualMemory.IsChecked = effective == MemoryMode.Manual;
        _syncing = false;

        AutoMemory.IsEnabled = ManualMemory.IsEnabled = !userFlags;
        UserFlagsWarn.IsVisible = userFlags;
        ManualBoxes.IsVisible = effective == MemoryMode.Manual;
        AutoDesc.IsVisible = effective == MemoryMode.Automatic;
        if (AutoDesc.IsVisible) AutoDesc.Text = PreviewText(config);
    }

    /// <summary>What Automatic would give the server if it started now.</summary>
    /// <remarks>
    /// Without asking the Java, which would mean starting a JVM on the UI thread: only a 32-bit Java
    /// changes the ceiling, and the console says so when it starts.
    /// </remarks>
    private static string PreviewText(ServerConfig config)
    {
        var mods = MemoryProfile.CountMods(config);
        var plan = MemoryProfile.Plan(new MemoryInputs(config, JavaService.JavaRuntime.Unknown, mods,
            SystemMemory.TotalMb(), MemoryBudget.ReservedByOthersMb(config.Id), MemoryPreferences.DedicatedMachine));
        return Localizer.Format("Mem_PlanNowFmt", ByteSize.Format(plan.CeilingMb * 1024 * 1024),
            ServerViewModel.ReasonText(plan.Reason, mods));
    }

    /// <inheritdoc />
    public void ShowError(string? message)
    {
        Error.Text = message;
        Error.IsVisible = message is not null;
    }

    private async void BrowseJava_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("Title_SelectJava"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java")
                {
                    Patterns = OperatingSystem.IsWindows() ? new[] { "java.exe" } : new[] { "java" }
                }
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path)) _draft.Config.JavaPath = path;
    }
}
