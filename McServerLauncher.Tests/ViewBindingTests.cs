using Avalonia.Controls;
using Avalonia.Logging;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;
using McServerLauncher.Views;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// Hosts the real views on real view models and fails on any binding that finds nothing.
/// </summary>
/// <remarks>
/// <para>
/// The views bind by name (<c>x:CompileBindings="False"</c>), so renaming or moving a property the
/// XAML reads compiles fine and just shows nothing. That is the failure a refactor of a view model
/// risks, and a unit test of the view model cannot see it: this is the check that can.
/// </para>
/// <para>
/// The server's detail page is part of <c>MainWindow</c>, which builds its own view model from the
/// user's data and is therefore not hosted here; what it binds is covered by the panels it is made
/// of (mods, backups, players, settings) and by the properties' own tests.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class ViewBindingTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-bind-" + Guid.NewGuid().ToString("N"));

    /// <summary>Collects the warnings Avalonia raises about bindings, which is where a miss is reported.</summary>
    private sealed class BindingSink : ILogSink
    {
        public readonly List<string> Misses = new();

        public bool IsEnabled(LogEventLevel level, string area) =>
            area == LogArea.Binding && level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Misses.Add(messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues) =>
            Misses.Add(Format(messageTemplate, propertyValues));

        /// <summary>Fills Avalonia's <c>{Name}</c> placeholders in order, enough to tell which binding it was.</summary>
        private static string Format(string template, object?[] values)
        {
            var i = 0;
            return System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]*\}",
                _ => i < values.Length ? values[i++]?.ToString() ?? "null" : "?");
        }
    }

    private ServerViewModel Server()
    {
        Directory.CreateDirectory(_folder);
        return new ServerViewModel(new ServerConfig { Name = "enlaces", FolderPath = _folder });
    }

    /// <summary>Shows <paramref name="view"/> on <paramref name="context"/> and returns the misses.</summary>
    private List<string> Misses(Func<(Control View, object Context)> build)
    {
        var sink = new BindingSink();
        var previous = Logger.Sink;
        List<string> found = null!;
        ui.Run(() =>
        {
            Logger.Sink = sink;
            try
            {
                var (view, context) = build();
                view.DataContext = context;
                AvaloniaFixture.WithoutIcons(view);
                // The window's context is the main view model in the app, and some panels bind to it
                // through their ancestor window (the backups panel's "backup settings" button).
                var window = new Window { Content = view, Width = 900, Height = 700, DataContext = new MainViewModel(_folder) };
                window.Show();
                window.UpdateLayout();
                AvaloniaFixture.Pump();
                window.UpdateLayout();
                window.Hide();   // not Close: see MainWindow.OnClosing
                found = sink.Misses.ToList();
            }
            finally { Logger.Sink = previous; }
        });
        return found;
    }

    /// <summary>Fails with every miss written out, since the one that matters is rarely the first.</summary>
    private static void NoMisses(IReadOnlyCollection<string> misses) =>
        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));

    [Fact]
    public void ABindingThatFindsNothingIsReported()
    {
        // The control for every other test here: with nothing to bind to, the panel must complain,
        // or a silent sink would make all of them pass.
        var misses = Misses(() => (new ServerModsView(), new object()));
        Assert.NotEmpty(misses);
    }

    [Fact]
    public void TheModsPanelBindsEverythingItReads()
    {
        var misses = Misses(() => { var s = Server(); return (new ServerModsView(), s.Mods); });
        NoMisses(misses);
    }

    [Fact]
    public void TheBackupsPanelBindsEverythingItReads()
    {
        var misses = Misses(() => { var s = Server(); return (new ServerBackupsView(), s.Backups); });
        NoMisses(misses);
    }

    [Fact]
    public void ThePlayersPanelBindsEverythingItReads()
    {
        var misses = Misses(() => (new PlayersTabView(), Server()));
        NoMisses(misses);
    }

    [Fact]
    public void TheServerCardBindsEverythingItReads()
    {
        var misses = Misses(() => (new ServerCardView(), Server()));
        NoMisses(misses);
    }

    [Fact]
    public void TheServerSettingsPageBindsEverythingItReads()
    {
        var misses = Misses(() =>
        {
            var s = Server();
            return (new ServerSettingsView(s, ServerSettingsPage.Game), s);
        });
        NoMisses(misses);
    }

    [Fact]
    public void TheTunnelsAndSettingsSectionsBindEverythingTheyRead()
    {
        MainViewModel? main = null;
        ui.Run(() => main = new MainViewModel(_folder));

        NoMisses(Misses(() => (new TunnelsView(), main!.Tunnels)));
        NoMisses(Misses(() => (new SettingsView(), main!.Settings)));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch { /* temp */ }
    }
}
