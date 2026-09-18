using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The Java address box: what it says while there is nothing to show, and what it refuses to do.
/// </summary>
/// <remarks>
/// <para>
/// Two reports that turned out to be the same box. The address "took ages to appear" — it was
/// usually there on playit's side within seconds, and the app simply was not going to ask again for
/// another half minute — and the box could be typed into, which never had any effect: the tunnel
/// keeps its name whatever is in there, and the next refresh overwrote it.
/// </para>
/// <para>
/// Both halves are about the same silence. An empty box that explains nothing, right after the
/// console has promised the address is seconds away, reads as a broken app; and a box that accepts
/// typing is an invitation to try to fix it by hand.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class TunnelAddressTests(AvaloniaFixture ui)
{
    [Fact]
    public void EveryStateSaysSomething()
    {
        foreach (var state in Enum.GetValues<TunnelAddressState>())
        {
            var key = TunnelAddressStates.KeyFor(state);
            var text = Localizer.Get(key);

            // Localizer.Get returns the key itself when it is missing, and the key is built at run
            // time, so LocalizationTests — which only reads string literals — cannot see these four.
            Assert.NotEqual(key, text);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    [Fact]
    public void TheFourStatesAreDistinct()
    {
        var keys = Enum.GetValues<TunnelAddressState>()
            .Select(TunnelAddressStates.KeyFor)
            .ToList();

        // Two states sharing a line means one of the three situations that used to be an empty box
        // is still unexplained.
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void WaitingIsWhatAServerStartsOut()
    {
        // Not Ready, and not NoTunnel: before the first lookup comes back the app knows nothing,
        // and either of the other two would be an assertion it has not earned. NoTunnel in
        // particular would flash "make one with the button" at a server with a perfectly good
        // tunnel, every time the app starts.
        WithServer(server => Assert.Equal(TunnelAddressState.Waiting, server.TunnelState));
    }

    [Fact]
    public void TheLineFollowsTheStateWithoutAnybodyAskingItTo()
    {
        WithServer(server =>
        {
            var seen = new List<string>();
            server.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? string.Empty);

            server.TunnelState = TunnelAddressState.Ready;

            Assert.Contains(nameof(server.TunnelStateText), seen);
            Assert.Equal(Localizer.Get("Tunnel_StateReady"), server.TunnelStateText);
        });
    }

    [Fact]
    public void TheAddressBoxCannotBeTypedInto()
    {
        var box = Regex.Matches(PlayitRowMarkup(), @"<TextBox\b[\s\S]*?/>")
            .Select(m => m.Value)
            .Single(v => v.Contains("{Binding TunnelAddress}", StringComparison.Ordinal));

        Assert.Contains("IsReadOnly=\"True\"", box);
    }

    [Fact]
    public void ThePlaceholderNoLongerInvitesWhatTheBoxRefuses()
    {
        // It used to read "auto-detected, or paste the tunnel address here". Leaving that under a
        // read-only box would be the app asking for something it then ignores — and the five
        // languages have to stop asking together, not just the one whoever made the change reads.
        foreach (var file in ResourceFiles)
        {
            var text = Value(file, "Tunnel_Placeholder");

            Assert.False(string.IsNullOrWhiteSpace(text));
            foreach (var verb in new[] { "pega", "paste", "cola", "colle", "einfügen" })
                Assert.DoesNotContain(verb, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheStateLineIsOnScreenWhateverTheState()
    {
        var line = Regex.Matches(PlayitRowMarkup(), @"<TextBlock\b[\s\S]*?/>")
            .Select(m => m.Value)
            .Single(v => v.Contains("{Binding TunnelStateText}", StringComparison.Ordinal));

        // No IsVisible on it. A line that only shows up when something is wrong is a line nobody
        // has ever read before, so it arrives looking like a new error rather than an explanation.
        Assert.DoesNotContain("IsVisible", line);
    }

    [Fact]
    public void EveryNameThePlayitRowBindsToExistsOnTheViewModel()
    {
        // MainWindow.axaml is x:CompileBindings="False": a mistyped name raises nothing and the
        // line simply stays empty, which is indistinguishable from the bug being unfixed.
        var names = Regex.Matches(PlayitRowMarkup(), @"\{Binding ([A-Za-z0-9_]+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        var vm = typeof(ServerViewModel);
        foreach (var name in names)
            Assert.True(vm.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null,
                $"ServerViewModel no tiene ninguna propiedad «{name}», que MainWindow.axaml enlaza");
    }

    // --- helpers ---

    private static readonly string[] ResourceFiles =
    {
        "Strings.resx", "Strings.en.resx", "Strings.pt.resx", "Strings.fr.resx", "Strings.de.resx"
    };

    /// <summary>One key's text straight out of one .resx, since Localizer only reads the current culture.</summary>
    private static string Value(string file, string key)
    {
        var path = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Resources", file);
        var entry = XDocument.Load(path).Root!.Elements("data")
            .FirstOrDefault(d => d.Attribute("name")?.Value == key);

        Assert.True(entry is not null, $"{file} no tiene la clave «{key}»");
        return entry!.Element("value")?.Value ?? string.Empty;
    }

    /// <summary>A server view model on the UI thread, shut down afterwards. Activate() is not called.</summary>
    private void WithServer(Action<ServerViewModel> assert) =>
        ui.Run(() =>
        {
            var config = new ServerConfig
            {
                Name = "tunel",
                FolderPath = Path.Combine(Path.GetTempPath(), "mcl-tunnel-" + Guid.NewGuid().ToString("N"))
            };
            Directory.CreateDirectory(config.FolderPath);

            var server = new ServerViewModel(config);
            try { assert(server); }
            finally
            {
                server.ShutdownAsync().GetAwaiter().GetResult();
                try { Directory.Delete(config.FolderPath, recursive: true); } catch { /* best-effort */ }
            }
        });

    /// <summary>The Playit card down to where the Bedrock half begins.</summary>
    private static string PlayitRowMarkup()
    {
        var view = Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml");
        var xaml = File.ReadAllText(view);

        var start = xaml.IndexOf("<!-- Playit.gg -->", StringComparison.Ordinal);
        Assert.True(start >= 0, "la tarjeta de Playit ya no está en MainWindow.axaml");

        var end = xaml.IndexOf("<!-- Bedrock:", start, StringComparison.Ordinal);
        Assert.True(end > start, "la tarjeta de Playit ya no lleva debajo el bloque de Bedrock");

        return xaml[start..end];
    }
}
