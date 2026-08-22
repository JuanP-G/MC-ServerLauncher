using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The wall crossplay hits on a modded server, and the app noticing it out loud.
/// </summary>
/// <remarks>
/// Geyser joins the Java server as a client with no mods. A NeoForge server carrying mods the
/// client must have rejects that connection during configuration, and the reason it logs tells the
/// player to install NeoForge — which, to somebody on a phone, is not advice at all. The launcher
/// cannot lift the restriction; these tests cover it explaining it instead.
/// </remarks>
public class CrossplayModdedClientTests
{
    /// <summary>The line a real server wrote, copied from a run where this actually happened.</summary>
    private const string RealKick =
        "[22ago2026 12:50:12.650] [Server thread/INFO] " +
        "[net.minecraft.server.network.ServerConfigurationPacketListenerImpl/]: " +
        ".GustoffotsuG617 (00000000-0000-0000-0009-01f255b019aa) lost connection: You are trying to " +
        "connect to a server that is running NeoForge, but you are not. Please install NeoForge " +
        "Version: 26.2.0.64 to connect to this server.";

    [Fact]
    public void TheKickIsRecognised() =>
        Assert.True(CrossplayDiagnostics.IsModdedClientRejection(RealKick));

    [Fact]
    public void OrdinaryDisconnectsAreNot()
    {
        // Every server logs these constantly. Explaining crossplay at each one would be noise.
        Assert.False(CrossplayDiagnostics.IsModdedClientRejection(
            "[12:38:50] [Server thread/INFO]: .GustoffotsuG617 lost connection: Disconnected"));
        Assert.False(CrossplayDiagnostics.IsModdedClientRejection(
            "[12:38:50] [Server thread/INFO]: Bob lost connection: Timed out"));
        Assert.False(CrossplayDiagnostics.IsModdedClientRejection(""));
    }

    [Fact]
    public void QuotingTheMessageIsNotTheEvent()
    {
        // A player pasting the error into chat, asking what it means.
        Assert.False(CrossplayDiagnostics.IsModdedClientRejection(
            "[12:40:00] [Server thread/INFO]: <Bob> it says please install NeoForge, what do i do"));
    }

    [Fact]
    public void TheAppsOwnExplanationDoesNotTriggerItAgain()
    {
        // The explanation is written to the same console it is read from. If it matched itself the
        // warning would feed on its own output, so this is a loop guard, not a wording check.
        foreach (var lang in new[] { "es", "en", "pt", "fr", "de" })
        {
            using var _ = new Culture(lang);
            Assert.False(CrossplayDiagnostics.IsModdedClientRejection(
                McServerLauncher.Localization.Localizer.Get("Msg_CrossplayModdedKick")));
        }
    }

    [Fact]
    public void OnlyTheModLoadersCarryTheWarning()
    {
        // Paper is the way out of this: plugins run only on the server, so there is no such thing
        // as a client missing them. Saying otherwise would push people away from the one type that
        // takes both content and Bedrock players.
        Assert.False(CrossplayService.ModsCanLockOutBedrock(ServerType.Paper));

        Assert.True(CrossplayService.ModsCanLockOutBedrock(ServerType.Fabric));
        Assert.True(CrossplayService.ModsCanLockOutBedrock(ServerType.NeoForge));
    }

    [Fact]
    public void EveryCrossplayTypeHasBeenDecidedAboutOneWayOrTheOther()
    {
        // A type added to SupportedTypes later must have this answered for it too, rather than
        // silently defaulting to "no warning" — which is the answer that loses people an evening.
        foreach (var type in GeyserConfigService.SupportedTypes)
            Assert.True(type is ServerType.Paper or ServerType.Fabric or ServerType.NeoForge,
                $"decide si {type} necesita el aviso de mods");
    }

    [Fact]
    public void TheNewStringsExistInEveryLanguage()
    {
        string[] keys = { "Msg_CrossplayModdedKick", "Crossplay_ModdedNote" };

        foreach (var lang in new[] { "es", "en", "pt", "fr", "de" })
        {
            using var _ = new Culture(lang);
            foreach (var key in keys)
            {
                var value = McServerLauncher.Localization.Localizer.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(value) || value == key, $"falta {key} en {lang}");
            }
        }
    }

    /// <summary>Switches the UI culture for one test and puts it back.</summary>
    private sealed class Culture : IDisposable
    {
        private readonly System.Globalization.CultureInfo _original =
            System.Globalization.CultureInfo.CurrentUICulture;

        public Culture(string name) =>
            System.Globalization.CultureInfo.CurrentUICulture =
                System.Globalization.CultureInfo.GetCultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentUICulture = _original;
    }
}
