using System.Diagnostics;
using System.Text.RegularExpressions;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The agent in a real JVM, started the way the app starts a server: from the server's folder, with the
/// relative flag the app builds. Needs a Java 12 or later (JAVA_HOME or on the PATH); the CI runners
/// have one, and on a machine without it the test has nothing to run and passes.
/// </summary>
[Collection("memory-agent")]
public sealed class MemoryAgentIntegrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-agent-it-" + Guid.NewGuid().ToString("N"));

    public MemoryAgentIntegrationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { /* temp */ }
    }

    private static string? FindJava()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home)
            candidates.Add(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"));
        candidates.Add("java");

        foreach (var java in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(java, "-version")
                {
                    RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                });
                if (p is null) continue;
                var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                var m = Regex.Match(text, @"version ""(\d+)");
                if (m.Success && int.Parse(m.Groups[1].Value) >= 12) return java;
            }
            catch { /* not there */ }
        }
        return null;
    }

    private static string? Wait(string path, Func<string, bool> done, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) is var text && done(text)) return text;
            }
            catch (IOException) { /* being replaced this instant */ }
            Thread.Sleep(500);
        }
        return null;
    }

    [Fact]
    public void The_agent_applies_the_control_file_in_a_real_JVM_and_reports_what_it_did()
    {
        if (FindJava() is not { } java) return;

        Assert.True(MemoryAgent.Install(_folder));
        MemoryAgent.WriteControl(_folder, HeapMargins.Tight);
        File.WriteAllText(Path.Combine(_folder, "Sleep.java"),
            "public class Sleep { public static void main(String[] a) throws Exception { Thread.sleep(60000); } }");

        var psi = new ProcessStartInfo(java, $"-Xmx128m {MemoryAgent.Flag} Sleep.java")
        {
            WorkingDirectory = _folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var jvm = Process.Start(psi)!;
        try
        {
            var status = Path.Combine(_folder, GcLogging.FolderName, "status.json");

            var tight = Wait(status, t => t.Contains("\"MinHeapFreeRatio\":5") && t.Contains("\"G1PeriodicGCInterval\":5000"), 40);
            Assert.True(tight is not null, "the agent never reported the tight margins: " + (File.Exists(status) ? File.ReadAllText(status) : "no status.json"));
            Assert.Contains("\"MaxHeapFreeRatio\":15", tight);
            Assert.Contains("\"lastError\":\"\"", tight);

            // And back: the app writes the relaxed margins and the agent restores them.
            MemoryAgent.WriteControl(_folder, HeapMargins.Relaxed);
            var relaxed = Wait(status, t => t.Contains("\"MinHeapFreeRatio\":10") && t.Contains("\"MaxHeapFreeRatio\":30"), 30);
            Assert.True(relaxed is not null, "the agent never restored the relaxed margins");
            Assert.Contains("\"G1PeriodicGCInterval\":15000", relaxed);

            var parsed = MemoryAgent.ReadStatus(_folder, DateTime.UtcNow, TimeSpan.FromSeconds(30));
            Assert.NotNull(parsed);
            Assert.True(parsed!.HeapMax > 0);
            Assert.Equal(10, parsed.Flags["MinHeapFreeRatio"]);
        }
        finally
        {
            try { jvm.Kill(true); } catch { /* already gone */ }
        }
    }

    [Fact]
    public void A_control_file_with_bad_values_changes_nothing_and_the_JVM_stays_up()
    {
        if (FindJava() is not { } java) return;

        Assert.True(MemoryAgent.Install(_folder));
        var dir = Path.Combine(_folder, GcLogging.FolderName);
        File.WriteAllText(Path.Combine(dir, "control"),
            "MinHeapFreeRatio=500\nMaxHeapFreeRatio=-3\nG1PeriodicGCInterval=abc\nSoftMaxHeapSize=1\nMaxHeapFreeRatio=20\nMinHeapFreeRatio=30\n");
        File.WriteAllText(Path.Combine(_folder, "Sleep.java"),
            "public class Sleep { public static void main(String[] a) throws Exception { Thread.sleep(30000); } }");

        var psi = new ProcessStartInfo(java, $"-Xmx128m -XX:MinHeapFreeRatio=40 -XX:MaxHeapFreeRatio=70 {MemoryAgent.Flag} Sleep.java")
        {
            WorkingDirectory = _folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var jvm = Process.Start(psi)!;
        try
        {
            var report = Wait(Path.Combine(dir, "status.json"), t => t.Contains("\"agent\""), 40);

            Assert.NotNull(report);
            // Out of range, not numbers, unknown options, and a maximum below the minimum: none applied.
            Assert.Contains("\"MinHeapFreeRatio\":40", report);
            Assert.Contains("\"MaxHeapFreeRatio\":70", report);
            Assert.False(jvm.HasExited);
        }
        finally
        {
            try { jvm.Kill(true); } catch { /* already gone */ }
        }
    }
}
