using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// The agent that holds a server's heap tighter while the machine is short of memory: getting it
/// into the server's folder, onto the command line, and out again when it must not run.
/// </summary>
/// <remarks>
/// <para>
/// The agent is a 10 KB jar built from <c>agent/</c> and embedded in the app, so nothing is downloaded
/// and no JDK is needed to publish. It only reads a small control file and writes a status file in the
/// server's own hidden folder; it opens no socket and calls no <c>System.gc()</c>.
/// </para>
/// <para>
/// It can stop a server from starting (a JVM refuses to start over an agent it cannot load), so there
/// are three ways to turn it off, and a way for it to turn itself off:
/// </para>
/// <list type="bullet">
/// <item>a Manual server never gets it, and neither does one on a Java that is not HotSpot or is
/// older than 12 (where the profile it adjusts does not apply);</item>
/// <item>the setting "adjust memory live" in Settings, and the environment variable
/// <c>MCSL_NO_AGENT=1</c>;</item>
/// <item>a start that dies within a minute of an agent error is noted against the pair of that Java
/// and this agent, and the server is restarted without it, here and in every start after.</item>
/// </list>
/// </remarks>
public static partial class MemoryAgent
{
    private const string ResourceName = "McServerLauncher.Resources.agent.mcsl-agent.jar";

    /// <summary>The jar's file name inside the server's hidden memory folder.</summary>
    public const string JarFileName = "mcsl-agent.jar";

    /// <summary>The file the app writes and the agent reads.</summary>
    public const string ControlFileName = "control";

    /// <summary>The file the agent writes and the app reads.</summary>
    public const string StatusFileName = "status.json";

    /// <summary>How long after starting an agent error still counts as the agent's doing.</summary>
    public static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(60);

    /// <summary>Where the failures are remembered. Settable so tests can point it at a temp folder.</summary>
    public static string FailuresPath { get; set; } = AppPaths.In("memory-agent-failures.json");

    /// <summary>The environment variable that turns the agent off for everything started from here.</summary>
    public const string DisableVariable = "MCSL_NO_AGENT";

    /// <summary>SHA-256 of the embedded jar, in lower case hex.</summary>
    public static string EmbeddedHash
    {
        get
        {
            using var stream = OpenEmbedded();
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
    }

    private static Stream OpenEmbedded() =>
        typeof(MemoryAgent).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The memory agent is not embedded in the app.");

    /// <summary>
    /// True when this server should start with the agent: it is Automatic, on a HotSpot from Java 12,
    /// the setting is on, the variable is not set, and this Java has not refused this agent before.
    /// </summary>
    public static bool ShouldUse(ServerConfig config, JavaService.JavaRuntime java, bool settingEnabled,
        Func<string, string?>? environment = null)
    {
        if (!settingEnabled) return false;
        if ((environment ?? Environment.GetEnvironmentVariable)(DisableVariable) is { Length: > 0 } v && v != "0") return false;
        if (MemoryProfile.Effective(config) != MemoryMode.Automatic) return false;
        if (java.IsOpenJ9 || java.Major < 12) return false;
        return !HasFailed(java.Major, EmbeddedHash);
    }

    /// <summary>The flag that loads the agent, with the path relative to the server's folder.</summary>
    public static string Flag =>
        $"-javaagent:{GcLogging.FolderName}/{JarFileName}={GcLogging.FolderName}";

    /// <summary>
    /// Puts the jar in the server's memory folder, replacing one that is missing or is not this build's,
    /// and writes the control file for the margins the server starts with. Returns false when it cannot,
    /// which means the flag must not be given: an agent that cannot be loaded stops the JVM.
    /// </summary>
    public static bool Install(string serverFolder)
    {
        try
        {
            var dir = Path.Combine(serverFolder, GcLogging.FolderName);
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, JarFileName);
            var wanted = EmbeddedHash;
            if (!File.Exists(path) || HashOf(path) != wanted)
            {
                var tmp = path + ".tmp";
                using (var source = OpenEmbedded())
                using (var target = File.Create(tmp))
                    source.CopyTo(target);
                File.Move(tmp, path, overwrite: true);
            }

            // Verified after writing, because the next thing that reads it is a JVM deciding whether to start.
            if (HashOf(path) != wanted) return false;

            WriteControl(serverFolder, HeapMargins.Relaxed);
            File.Delete(Path.Combine(dir, StatusFileName));   // a status from the last run says nothing about this one
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes the control file for these margins, by writing a temporary file and renaming it.</summary>
    public static void WriteControl(string serverFolder, HeapMargins margins)
    {
        var dir = Path.Combine(serverFolder, GcLogging.FolderName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ControlFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, MemoryAgentPolicy.ControlText(margins));
        File.Move(tmp, path, overwrite: true);
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // --- what it reports ---

    /// <summary>The agent's latest report, or null when there is none or it is older than <paramref name="maxAge"/>.</summary>
    public static AgentStatus? ReadStatus(string serverFolder, DateTime nowUtc, TimeSpan maxAge)
    {
        try
        {
            var path = Path.Combine(serverFolder, GcLogging.FolderName, StatusFileName);
            if (!File.Exists(path)) return null;
            var status = JsonSerializer.Deserialize<AgentStatus>(File.ReadAllText(path));
            if (status is null) return null;
            var written = DateTimeOffset.FromUnixTimeMilliseconds(status.Time).UtcDateTime;
            return nowUtc - written <= maxAge ? status : null;
        }
        catch
        {
            return null;   // being replaced this instant, or not valid: the next look finds it
        }
    }

    // --- when it does not load ---

    /// <summary>True for a console line that says the JVM could not load an agent.</summary>
    /// <remarks>
    /// The three ways a JVM says so: it cannot open the jar, the agent library failed to start, and the
    /// native method that runs <c>premain</c> failed. The lines carry no log prefix, so they are matched
    /// by their own words at the very start of the line, which a player's chat (always prefixed) never is.
    /// </remarks>
    public static bool IsLoadError(string line) => LoadError().IsMatch(line);

    [GeneratedRegex(@"^(?:Error opening zip file or JAR manifest missing|agent library failed|FATAL ERROR in native method: processing of -javaagent)")]
    private static partial Regex LoadError();

    /// <summary>True when this Java has refused this agent before.</summary>
    public static bool HasFailed(int javaMajor, string hash) => ReadFailures().Contains(Key(javaMajor, hash));

    /// <summary>Notes that this Java could not load this agent, so no start uses it again.</summary>
    public static void RecordFailure(int javaMajor, string hash)
    {
        try
        {
            var failures = ReadFailures();
            if (!failures.Add(Key(javaMajor, hash))) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FailuresPath)!);
            AtomicJsonFile.Write(FailuresPath, failures.OrderBy(f => f, StringComparer.Ordinal).ToList());
        }
        catch { /* not being able to note it only means the next start tries once more */ }
    }

    private static HashSet<string> ReadFailures()
    {
        var (loaded, _) = AtomicJsonFile.Load<List<string>>(FailuresPath);
        return new HashSet<string>(loaded ?? [], StringComparer.Ordinal);
    }

    private static string Key(int javaMajor, string hash) => $"java{javaMajor}:{hash}";
}

/// <summary>What the agent says about the JVM it runs in. Read from <c>status.json</c>.</summary>
public sealed class AgentStatus
{
    [System.Text.Json.Serialization.JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("time")] public long Time { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("java")] public string Java { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("heapUsed")] public long HeapUsed { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("heapCommitted")] public long HeapCommitted { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("heapMax")] public long HeapMax { get; set; }

    /// <summary>What was alive after the last collections, in bytes; null before the first one.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("liveAfterGc")] public long? LiveAfterGc { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("gcCount")] public long GcCount { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gcTimeMs")] public long GcTimeMs { get; set; }

    /// <summary>The options in effect: <c>MinHeapFreeRatio</c>, <c>MaxHeapFreeRatio</c>, <c>G1PeriodicGCInterval</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("flags")] public Dictionary<string, long> Flags { get; set; } = new();

    /// <summary>The last thing the agent could not do, or empty.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastError")] public string LastError { get; set; } = "";
}
