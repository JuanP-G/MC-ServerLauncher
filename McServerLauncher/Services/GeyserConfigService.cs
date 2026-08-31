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
/// Geyser works out the Java server's address and port on its own. It does <em>not</em> work out
/// the authentication: a server with Floodgate installed sat on <c>auth-type: online</c> for eight
/// days, so Geyser tried to authenticate against Mojang, had no account to do it with, and Floodgate
/// turned every Bedrock player away asking whether it was configured correctly. The launcher writes
/// that setting rather than trusting it to be detected.
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
        { ServerType.Paper, ServerType.Purpur, ServerType.Fabric, ServerType.NeoForge };

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
        // Every Bukkit-family server runs the same Geyser-Spigot build, so they share its folder;
        // Purpur is a Paper fork and behaves as Paper here in every respect.
        var relative = type switch
        {
            ServerType.Paper or ServerType.Purpur => Path.Combine("plugins", "Geyser-Spigot"),
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
    public static string MinimalConfig(int bedrockPort, int? broadcastPort, bool floodgate)
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

        // "java:", not "remote:". Geyser 2.11 renamed the section, and a config still writing
        // the old name is simply ignored — which is what was happening: the file had no "remote"
        // in it at all, because Geyser had regenerated it under the new name.
        lines.Add("java:");
        lines.Add(floodgate
            ? "  # Floodgate is installed, so Bedrock players are checked against it rather than Mojang."
            : "  # No Floodgate installed: Bedrock players would need their own Java account.");
        lines.Add($"  auth-type: {(floodgate ? "floodgate" : "online")}");
        lines.Add("");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Sets the authentication Geyser uses towards the Java server, and where to find the key.
    /// </summary>
    /// <param name="yaml">The current contents of config.yml.</param>
    /// <param name="floodgate">Whether Floodgate is installed beside Geyser.</param>
    /// <param name="keyPath">
    /// Where Floodgate actually leaves its key, relative to Geyser's own config folder. Pointed at
    /// rather than copied: Floodgate can regenerate it, and a copy would go stale silently.
    /// </param>
    /// <remarks>
    /// Same line surgery as <see cref="SetBedrockPorts"/>, so the comments Geyser ships — which are
    /// its real documentation — survive being edited.
    /// </remarks>
    public static string SetJavaAuth(string yaml, bool floodgate, string? keyPath = null)
    {
        var newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();

        var section = FindSection(lines, "java:");
        if (section is null)
        {
            var appended = yaml.TrimEnd('\r', '\n') + newline + newline
                           + JavaSection(floodgate).Replace("\n", newline);
            return appended;
        }

        var (start, end) = section.Value;
        SetKey(lines, start, end, "auth-type", floodgate ? "floodgate" : "online");

        var result = string.Join(newline, lines);

        // The key lives under "advanced:", a different section, and only matters with Floodgate.
        if (floodgate && keyPath is not null)
            result = SetFloodgateKey(result, keyPath);

        return result;
    }

    /// <summary>Points Geyser at the key Floodgate generated, wherever that turned out to be.</summary>
    /// <remarks>
    /// Geyser resolves this relative to its own config folder and its comment says the plugin
    /// version is picked up automatically. The mod version is not: Floodgate writes to
    /// <c>config/floodgate/key.pem</c> while Geyser looks beside itself and finds nothing.
    /// </remarks>
    private static string SetFloodgateKey(string yaml, string keyPath)
    {
        var newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();

        // Not inside a top-level section of its own: it sits under "advanced:", so the whole file
        // is searched for the key rather than a range.
        var index = IndexOfKey(lines, -1, lines.Count, "floodgate-key-file", commented: false);
        if (index < 0) index = IndexOfKey(lines, -1, lines.Count, "floodgate-key-file", commented: true);
        if (index < 0) return yaml;   // an older or trimmed config: leave it alone

        var indent = Indent(lines[index]);
        if (indent.Length == 0) indent = "  ";
        lines[index] = indent + "floodgate-key-file: " + keyPath;

        return string.Join(newline, lines);
    }

    /// <summary>The java section as written into a config that has none.</summary>
    private static string JavaSection(bool floodgate) =>
        "java:\n  auth-type: " + (floodgate ? "floodgate" : "online") + "\n";

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
    /// <param name="floodgate">
    /// Whether Floodgate is installed beside Geyser, which decides the authentication mode written
    /// into the java section: Bedrock players need "floodgate" there, and "online" turns every one
    /// of them away.
    /// </param>
    /// <remarks>
    /// Edited line by line rather than parsed and re-emitted. A YAML round-trip would drop every
    /// comment in the file, and in Geyser's config the comments <em>are</em> the documentation —
    /// the next person to open it by hand would find it stripped bare.
    /// </remarks>
    public static string SetBedrockPorts(string yaml, int bedrockPort, int? broadcastPort, bool floodgate = true)
    {
        var newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();

        var section = FindSection(lines, "bedrock:");
        if (section is null)
        {
            // No bedrock section at all: append a whole one rather than guess where it belongs.
            var appended = yaml.TrimEnd('\r', '\n') + newline + newline
                           + MinimalConfig(bedrockPort, broadcastPort, floodgate).Replace("\n", newline);
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
            // The commented line's own indentation, unchanged. It used to add two for the "# " that
            // is being dropped, which is only right if the # sits in column zero — and in Geyser's
            // real config it does not: the line is "  # broadcast-port: 19132", so the key came out
            // at four spaces beside siblings at two, and YAML refuses to parse that.
            lines[commented] = Indent(lines[commented]) + key + ": " + value;
            return;
        }

        lines.Insert(start + 1, SectionIndent(lines, start, end) + key + ": " + value);
    }

    /// <summary>
    /// The indentation the keys of a section use, taken from the section itself.
    /// </summary>
    /// <remarks>
    /// Two spaces is what Geyser ships and what the fallback assumes, but reading it from a sibling
    /// costs nothing and means a config indented some other way still comes out valid — the same
    /// mistake as the commented branch above, one line later.
    /// </remarks>
    private static string SectionIndent(List<string> lines, int start, int end)
    {
        for (var i = start + 1; i < end && i < lines.Count; i++)
            if (lines[i].TrimStart().Length > 0)
                return Indent(lines[i]);

        return "  ";
    }

    private static void RemoveKey(List<string> lines, int start, int end, string key)
    {
        var live = IndexOfKey(lines, start, end, key, commented: false);
        if (live >= 0) lines.RemoveAt(live);
    }

    /// <summary>Finds a key line inside a section, live or commented out. -1 when absent.</summary>
    private static int IndexOfKey(List<string> lines, int start, int end, string key, bool commented)
    {
        for (var i = Math.Max(0, start + 1); i < end && i < lines.Count; i++)
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
