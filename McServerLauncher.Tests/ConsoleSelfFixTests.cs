using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Console warnings the app answers by itself, because they are its to answer.
/// </summary>
/// <remarks>
/// <para>
/// Most of what a modded server warns about on start is not the app's business — a mod's packaging,
/// a mixin aimed at a mod that is not installed, a Windows registry value. Two are: the JVM asking
/// for a flag on the command line the app builds, and BlueMap waiting for a consent the app can ask
/// for. Those two it now handles.
/// </para>
/// </remarks>
public class ConsoleSelfFixTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcl-selffix-" + Guid.NewGuid().ToString("N"));

    public ConsoleSelfFixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // --- The JVM's native-access warning ---

    [Fact]
    public void FromJava22TheAppEnablesNativeAccessItself()
    {
        // The warning says a future release will block the call instead of warning about it — the
        // server would stop working on a Java update with no change of its own.
        Assert.Contains("--enable-native-access=ALL-UNNAMED", ServerProcessManager.ImpliedJvmFlags(22, null));
        Assert.Contains("--enable-native-access=ALL-UNNAMED", ServerProcessManager.ImpliedJvmFlags(25, null));
    }

    [Theory]
    [InlineData(21)]
    [InlineData(17)]
    [InlineData(8)]
    [InlineData(0)]   // could not read the version
    public void BeforeJava22OrUnknownNothingIsAdded(int major)
    {
        // An option a JVM does not recognise stops it from starting at all — a far worse outcome than
        // any warning. So nothing is added where the flag is not known to be understood.
        Assert.Empty(ServerProcessManager.ImpliedJvmFlags(major, null));
    }

    [Fact]
    public void TheUsersOwnSettingStands()
    {
        Assert.Empty(ServerProcessManager.ImpliedJvmFlags(25, "--enable-native-access=com.example"));
    }

    [Fact]
    public void TheFlagGoesBeforeTheJarWhereTheJvmReadsIt()
    {
        var config = new ServerConfig { JarFile = "fabric-server.jar", MinRamGb = 4, MaxRamGb = 6 };

        var args = ServerProcessManager.BuildJavaArguments(config, javaMajor: 25);

        Assert.Equal("-Xms4G -Xmx6G --enable-native-access=ALL-UNNAMED -jar \"fabric-server.jar\" nogui", args);
    }

    [Fact]
    public void TheUnsafeMemoryFlagIsDeliberatelyNotAdded()
    {
        // Its accepted values are changing release by release; one a later JVM refuses would stop the
        // server starting over what is, today, a cosmetic warning about a library's code.
        Assert.DoesNotContain(ServerProcessManager.ImpliedJvmFlags(25, null),
            f => f.Contains("sun-misc-unsafe", StringComparison.Ordinal));
    }

    // --- BlueMap's download consent ---

    [Fact]
    public void BlueMapsWarningIsRecognised()
    {
        Assert.True(BlueMapConsent.IsAskingForConsent(
            "[21:06:02] [BlueMap-Plugin-Loading/WARN]: You must accept the required file download in order for BlueMap to work!"));
        Assert.False(BlueMapConsent.IsAskingForConsent(
            "[21:06:02] [BlueMap-Plugin-Loading/WARN]: BlueMap is missing important resources!"));
    }

    [Fact]
    public void AcceptingChangesThatOneValueAndNothingElse()
    {
        const string conf =
            "# Core-Configuration for BlueMap\n" +
            "# By changing the setting (accept-download) below to TRUE you are indicating that you agree\n" +
            "accept-download: false\n" +
            "\n" +
            "render-thread-count: 1\n";

        var accepted = BlueMapConsent.Accept(conf);

        Assert.Equal(conf.Replace("accept-download: false", "accept-download: true"), accepted);
    }

    [Theory]
    [InlineData("accept-download = false\n")]
    [InlineData("  accept-download:false\n")]
    public void OtherLegalSpellingsAreUnderstood(string conf)
    {
        Assert.Contains("true", BlueMapConsent.Accept(conf));
    }

    [Theory]
    [InlineData("accept-download: true\n")]                    // already accepted
    [InlineData("render-thread-count: 1\n")]                   // the setting is not there
    [InlineData("# accept-download: false\n")]                 // only mentioned in a comment
    public void WhenThereIsNoFalseToFlipNothingIsWritten(string conf)
    {
        // A file somebody edited by hand is theirs. Writing a new line into HOCON the app cannot fully
        // read could leave BlueMap refusing to load at all.
        Assert.Null(BlueMapConsent.Accept(conf));
    }

    [Fact]
    public void TheConfigIsFoundWhereverBlueMapKeepsIt()
    {
        // As a mod it reads config/bluemap/, as a plugin plugins/BlueMap/.
        var asMod = Path.Combine(_root, "mod");
        Directory.CreateDirectory(Path.Combine(asMod, "config", "bluemap"));
        File.WriteAllText(Path.Combine(asMod, "config", "bluemap", "core.conf"), "accept-download: false");

        var asPlugin = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(Path.Combine(asPlugin, "plugins", "BlueMap"));
        File.WriteAllText(Path.Combine(asPlugin, "plugins", "BlueMap", "core.conf"), "accept-download: false");

        Assert.EndsWith(Path.Combine("config", "bluemap", "core.conf"), BlueMapConsent.FindConfig(asMod));
        Assert.EndsWith(Path.Combine("plugins", "BlueMap", "core.conf"), BlueMapConsent.FindConfig(asPlugin));
        Assert.Null(BlueMapConsent.FindConfig(_root));
    }
}
