using System.Collections.Generic;
using System.IO;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Geyser's <c>config.yml</c>: where it lives, and the two ports the launcher has to set in it.
/// </summary>
/// <remarks>
/// <para>
/// Almost nothing needs configuring, because Geyser configures itself: with <c>remote.address</c>
/// left at <c>auto</c> it picks up the Java server's address, port and auth type, and if Floodgate
/// is installed alongside it switches to Floodgate authentication and finds its key on its own.
/// </para>
/// <para>
/// Two things it cannot work out by itself. The first is which local UDP port to listen on, when
/// the default 19132 is taken by another server. The second is <c>broadcast-port</c>, and that one
/// matters more than it looks: behind a Playit tunnel the port players connect to is the tunnel's
/// public port, never the local one, and Geyser's own comment says not to touch this setting unless
/// exactly that is true. Left alone, the Bedrock server list advertises a port that does not work.
/// The launcher is the one component that knows both numbers, because it creates the tunnel.
/// </para>
/// </remarks>
public static class GeyserConfigService
{
    /// <summary>The server types Geyser publishes a build for.</summary>
    /// <remarks>
    /// Forge and Vanilla are absent because Geyser genuinely has no build for them — not an
    /// oversight here. Vanilla has no plugin or mod loader at all, and Geyser dropped Forge in
    /// favour of NeoForge. Both could in principle be served by Geyser Standalone, which is a
    /// separate process to supervise, and that is deliberately out of scope.
    /// </remarks>
    public static readonly ServerType[] SupportedTypes =
        { ServerType.Paper, ServerType.Fabric, ServerType.NeoForge };

    /// <summary>Whether crossplay can be offered at all for this kind of server.</summary>
    public static bool Supports(ServerType type) => SupportedTypes.Contains(type);

    /// <summary>
    /// Geyser's config file for a server, or null for a type it cannot run on.
    /// </summary>
    /// <remarks>
    /// Plugins keep their configuration under <c>plugins/</c> and mods under <c>config/</c>, and
    /// each Geyser build names its own folder after the platform it was built for.
    /// </remarks>
    public static string? ConfigPath(string serverFolder, ServerType type)
    {
        var relative = type switch
        {
            ServerType.Paper => Path.Combine("plugins", "Geyser-Spigot"),
            ServerType.Fabric => Path.Combine("config", "Geyser-Fabric"),
            ServerType.NeoForge => Path.Combine("config", "Geyser-NeoForge"),
            _ => null
        };

        return relative is null ? null : Path.Combine(serverFolder, relative, "config.yml");
    }

    /// <summary>The smallest config that works, for before Geyser has ever run.</summary>
    /// <remarks>
    /// Geyser writes a full commented config on first start and fills in whatever a hand-written
    /// one leaves out, so this only needs to carry the parts it could not have guessed. Written
    /// before the first start so crossplay works on the first run rather than the second.
    /// </remarks>
    public static string MinimalConfig(int bedrockPort, int? broadcastPort)
    {
        var lines = new List<string>
        {
            "# Written by MC Server Launcher. Geyser fills in everything else on first start.",
            "bedrock:",
            $"  port: {bedrockPort}",
        };

        if (broadcastPort is { } advertised)
        {
            lines.Add("  # The port players actually connect to (the tunnel's public port), which is");
            lines.Add("  # not the local one above.");
            lines.Add($"  broadcast-port: {advertised}");
        }

        lines.Add("remote:");
        lines.Add("  # auto: Geyser takes the address, port and auth type from the server it runs on,");
        lines.Add("  # and switches to Floodgate by itself when Floodgate is installed alongside.");
        lines.Add("  address: auto");
        lines.Add("");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Returns <paramref name="yaml"/> with the two Bedrock ports set, leaving everything else —
    /// comments included — exactly as it was.
    /// </summary>
    /// <param name="yaml">The current contents of config.yml.</param>
    /// <param name="bedrockPort">The local UDP port Geyser should listen on.</param>
    /// <param name="broadcastPort">
    /// The port players connect to. Null removes the setting, for a server no longer behind a
    /// tunnel: leaving a stale one behind would advertise a port that stopped existing.
    /// </param>
    /// <remarks>
    /// Edited line by line rather than parsed and re-emitted. A YAML round-trip would drop every
    /// comment in the file, and in Geyser's config the comments <em>are</em> the documentation —
    /// the next person to open it by hand would find it stripped bare.
    /// </remarks>
    public static string SetBedrockPorts(string yaml, int bedrockPort, int? broadcastPort)
    {
        var newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();

        var section = FindSection(lines, "bedrock:");
        if (section is null)
        {
            // No bedrock section at all: append a whole one rather than guess where it belongs.
            var appended = yaml.TrimEnd('\r', '\n') + newline + newline
                           + MinimalConfig(bedrockPort, broadcastPort).Replace("\n", newline);
            return appended;
        }

        var (start, end) = section.Value;

        SetKey(lines, start, end, "port", bedrockPort.ToString());

        if (broadcastPort is { } advertised)
            SetKey(lines, start, end, "broadcast-port", advertised.ToString());
        else
            RemoveKey(lines, start, end, "broadcast-port");

        return string.Join(newline, lines);
    }

    // --- the line surgery ---

    /// <summary>The half-open range of lines belonging to a top-level section.</summary>
    private static (int Start, int End)? FindSection(List<string> lines, string header)
    {
        var start = lines.FindIndex(l => l.TrimEnd() == header);
        if (start < 0) return null;

        // The section ends at the next line that starts in column zero and isn't blank: YAML
        // nesting is indentation, so anything indented still belongs to this section.
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
            return (start, i);
        }
        return (start, lines.Count);
    }

    /// <summary>
    /// Sets <paramref name="key"/> inside a section, whether it is already there, commented out,
    /// or missing entirely.
    /// </summary>
    /// <remarks>
    /// Handling the commented-out case is the whole point for <c>broadcast-port</c>: Geyser ships
    /// it commented, so a naive "find the line and replace it" would find nothing and add a second
    /// one, leaving the file with a commented setting and a live one saying different things.
    /// </remarks>
    private static void SetKey(List<string> lines, int start, int end, string key, string value)
    {
        var live = IndexOfKey(lines, start, end, key, commented: false);
        if (live >= 0)
        {
            lines[live] = Indent(lines[live]) + key + ": " + value;
            return;
        }

        var commented = IndexOfKey(lines, start, end, key, commented: true);
        if (commented >= 0)
        {
            // Reuse the commented line's own indentation, minus the comment marker.
            var indent = new string(' ', Indent(lines[commented]).Length + 2);
            lines[commented] = indent + key + ": " + value;
            return;
        }

        lines.Insert(start + 1, "  " + key + ": " + value);
    }

    private static void RemoveKey(List<string> lines, int start, int end, string key)
    {
        var live = IndexOfKey(lines, start, end, key, commented: false);
        if (live >= 0) lines.RemoveAt(live);
    }

    /// <summary>Finds a key line inside a section, live or commented out. -1 when absent.</summary>
    private static int IndexOfKey(List<string> lines, int start, int end, string key, bool commented)
    {
        for (var i = start + 1; i < end && i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();

            if (commented)
            {
                if (!trimmed.StartsWith('#')) continue;
                trimmed = trimmed.TrimStart('#').TrimStart();
            }
            else if (trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith(key + ":", StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];
}
