using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Editing Geyser's config.yml.
/// </summary>
/// <remarks>
/// The failure mode here is silent: a wrong or missing <c>broadcast-port</c> produces a server that
/// starts, appears in the Bedrock list, and simply cannot be joined — with nothing anywhere saying
/// why. So the edits are pinned rather than eyeballed.
/// </remarks>
public class GeyserConfigTests
{
    /// <summary>
    /// The relevant part of Geyser's real shipped config, comments and all — including
    /// <c>broadcast-port</c> commented out, which is how it actually arrives.
    /// </summary>
    private const string RealConfig = """
        # --------------------------------
        # Geyser Configuration File
        # --------------------------------

        bedrock:
          # The IP address that will listen for connections.
          #address: 0.0.0.0
          # The port that will listen for connections
          port: 19132
          # Some hosting services change your Java port everytime you start the server.
          clone-remote-port: false
          # The MOTD that will be broadcasted to Minecraft: Bedrock Edition clients.
          motd1: "Geyser"
          # The port to broadcast to Bedrock clients with the MOTD that they should use to connect.
          # DO NOT uncomment and change this unless Geyser runs on a different internal port.
          # broadcast-port: 19132
          # Whether to enable PROXY protocol or not for clients.
          enable-proxy-protocol: false
        remote:
          # The IP address of the remote (Java Edition) server
          address: auto
          port: 25565
          auth-type: online

        floodgate-key-file: key.pem
        """;

    private static string[] Lines(string yaml) =>
        yaml.Replace("\r\n", "\n").Split('\n');

    private static string? ValueOf(string yaml, string key) =>
        Lines(yaml)
            .Select(l => l.TrimStart())
            .FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            ?.Split(':', 2)[1].Trim();

    // --- the two ports ---

    [Fact]
    public void SetsTheLocalPortInPlace()
    {
        var result = GeyserConfigService.SetBedrockPorts(RealConfig, 19140, 51234);

        Assert.Equal("19140", ValueOf(result, "port"));
    }

    [Fact]
    public void UncommentsBroadcastPortRatherThanAddingASecondOne()
    {
        // Geyser ships broadcast-port commented out. A naive replace would not find it, add its own
        // line, and leave the file with a commented setting and a live one disagreeing.
        var result = GeyserConfigService.SetBedrockPorts(RealConfig, 19132, 51234);

        var live = Lines(result).Count(l => l.TrimStart().StartsWith("broadcast-port:", StringComparison.Ordinal));
        Assert.Equal(1, live);
        Assert.Equal("51234", ValueOf(result, "broadcast-port"));
    }

    [Fact]
    public void SetsBroadcastPortWhenItIsAlreadyLive()
    {
        var once = GeyserConfigService.SetBedrockPorts(RealConfig, 19132, 51234);
        var twice = GeyserConfigService.SetBedrockPorts(once, 19132, 60000);

        Assert.Equal("60000", ValueOf(twice, "broadcast-port"));
        Assert.Equal(1, Lines(twice).Count(l => l.TrimStart().StartsWith("broadcast-port:", StringComparison.Ordinal)));
    }

    [Fact]
    public void RemovesBroadcastPortWhenThereIsNoLongerATunnel()
    {
        // Leaving a stale one behind would advertise a port that stopped existing.
        var withTunnel = GeyserConfigService.SetBedrockPorts(RealConfig, 19132, 51234);

        var without = GeyserConfigService.SetBedrockPorts(withTunnel, 19132, null);

        Assert.Equal(0, Lines(without).Count(l => l.TrimStart().StartsWith("broadcast-port:", StringComparison.Ordinal)));
    }

    // --- everything else must survive ---

    [Fact]
    public void EveryCommentIsKept()
    {
        // In Geyser's config the comments are the documentation. A YAML round-trip would strip them
        // all, and whoever opened the file next would find it bare.
        var before = Lines(RealConfig).Count(l => l.TrimStart().StartsWith('#'));

        var result = GeyserConfigService.SetBedrockPorts(RealConfig, 19140, 51234);
        var after = Lines(result).Count(l => l.TrimStart().StartsWith('#'));

        // One comment becomes the live broadcast-port line; the rest stay.
        Assert.Equal(before - 1, after);
        Assert.Contains("# Geyser Configuration File", result);
        Assert.Contains("DO NOT uncomment", result);
    }

    [Fact]
    public void OtherSettingsAreNotTouched()
    {
        var result = GeyserConfigService.SetBedrockPorts(RealConfig, 19140, 51234);

        Assert.Equal("auto", ValueOf(result, "address"));
        Assert.Equal("online", ValueOf(result, "auth-type"));
        Assert.Equal("false", ValueOf(result, "clone-remote-port"));
        Assert.Equal("key.pem", ValueOf(result, "floodgate-key-file"));
    }

    [Fact]
    public void TheRemotePortIsNotMistakenForTheBedrockOne()
    {
        // Both sections have a "port:" key. Editing the wrong one would point Geyser at the wrong
        // Java server and is exactly the mistake a whole-file search-and-replace would make.
        var result = GeyserConfigService.SetBedrockPorts(RealConfig, 19140, 51234);

        var remoteIndex = Array.FindIndex(Lines(result), l => l.TrimEnd() == "remote:");
        var remotePort = Lines(result).Skip(remoteIndex)
            .First(l => l.TrimStart().StartsWith("port:", StringComparison.Ordinal));

        Assert.Equal("  port: 25565", remotePort.TrimEnd());
    }

    // --- shapes the file can arrive in ---

    [Fact]
    public void AConfigWithNoBedrockSectionGetsOne()
    {
        var result = GeyserConfigService.SetBedrockPorts("remote:\n  address: auto\n", 19140, 51234);

        Assert.Contains("bedrock:", result);
        Assert.Equal("19140", ValueOf(result, "port"));
        Assert.Equal("51234", ValueOf(result, "broadcast-port"));
        Assert.Contains("address: auto", result);       // what was there stays
    }

    [Fact]
    public void WindowsLineEndingsAreKept()
    {
        var crlf = RealConfig.Replace("\n", "\r\n");

        var result = GeyserConfigService.SetBedrockPorts(crlf, 19140, 51234);

        Assert.Contains("\r\n", result);
        Assert.DoesNotContain(result.Replace("\r\n", ""), "\n");
    }

    [Fact]
    public void TheMinimalConfigIsEnoughToStartFrom()
    {
        // Written before Geyser has ever run, so crossplay works on the first start and not the
        // second. It must carry the two things Geyser cannot work out for itself.
        var yaml = GeyserConfigService.MinimalConfig(19140, 51234);

        Assert.Equal("19140", ValueOf(yaml, "port"));
        Assert.Equal("51234", ValueOf(yaml, "broadcast-port"));
        Assert.Equal("auto", ValueOf(yaml, "address"));
    }

    [Fact]
    public void TheMinimalConfigCanBePatchedAfterwards()
    {
        var yaml = GeyserConfigService.MinimalConfig(19140, 51234);

        var patched = GeyserConfigService.SetBedrockPorts(yaml, 19140, 60000);

        Assert.Equal("60000", ValueOf(patched, "broadcast-port"));
        Assert.Equal(1, Lines(patched).Count(l => l.TrimStart().StartsWith("broadcast-port:", StringComparison.Ordinal)));
    }

    // --- which servers can do this at all ---

    [Theory]
    [InlineData(ServerType.Paper, true)]
    [InlineData(ServerType.Fabric, true)]
    [InlineData(ServerType.NeoForge, true)]
    [InlineData(ServerType.Forge, false)]
    [InlineData(ServerType.Vanilla, false)]
    public void OnlyTheTypesGeyserShipsForAreSupported(ServerType type, bool expected)
    {
        // Forge and Vanilla are not an omission: Geyser publishes no build for either. Pinned so
        // that adding a server type forces a decision rather than silently defaulting to "no".
        Assert.Equal(expected, GeyserConfigService.Supports(type));
        Assert.Equal(expected, GeyserConfigService.ConfigPath("/srv", type) is not null);
    }

    [Theory]
    [InlineData(ServerType.Fabric, true)]
    [InlineData(ServerType.NeoForge, true)]
    [InlineData(ServerType.Paper, false)]
    public void FloodgateComesFromTheOnlySourceThatHasIt(ServerType type, bool fromModrinth)
    {
        // Not a preference between two equivalent sources. Modrinth's "floodgate" is
        // GeyserMC/Floodgate-Modded — Fabric and NeoForge only — while the Spigot build that Paper
        // needs exists solely on GeyserMC's own downloads site. Asking the wrong one returns
        // nothing, which is how the first version of this would have failed on Paper: the very
        // server type most people would choose for crossplay.
        Assert.Equal(fromModrinth, CrossplayService.FloodgateComesFromModrinth(type));
    }

    [Fact]
    public void EverySupportedTypeHasAFloodgateSource()
    {
        // Whatever the answer is, there has to be one: a server with Geyser and no Floodgate turns
        // every Bedrock player away unless they own Minecraft Java, which reads as crossplay being
        // broken rather than half-installed.
        foreach (var type in GeyserConfigService.SupportedTypes)
        {
            var modded = CrossplayService.FloodgateComesFromModrinth(type);
            Assert.True(modded || type == ServerType.Paper,
                $"{type} no tiene fuente de Floodgate definida");
        }
    }

    [Fact]
    public void EachSupportedTypeHasItsOwnConfigPath()
    {
        var paths = GeyserConfigService.SupportedTypes
            .Select(t => GeyserConfigService.ConfigPath("/srv", t))
            .ToList();

        Assert.All(paths, p => Assert.NotNull(p));
        Assert.Equal(paths.Count, paths.Distinct().Count());

        // Plugins live under plugins/, mods under config/ — getting these the wrong way round
        // writes a file Geyser never reads.
        Assert.Contains("plugins", GeyserConfigService.ConfigPath("/srv", ServerType.Paper)!);
        Assert.Contains("config", GeyserConfigService.ConfigPath("/srv", ServerType.Fabric)!);
    }
}
