using System.Diagnostics;

namespace McServerLauncher.Tests;

/// <summary>
/// The Playit partner proxy (<c>playit-proxy/worker.js</c>), actually run.
/// </summary>
/// <remarks>
/// <para>
/// The Worker holds the partner Api-Key and is the one piece of this repository that anyone on the
/// internet can call, so its rules (no limiter, no forwarding; bytes, not characters; only the
/// validated fields reach Playit) are tested where they live: <c>worker.test.mjs</c>, run with
/// <c>node --test</c>, no dependencies. This class only makes the normal test run include it, so CI
/// covers the Worker without a second workflow.
/// </para>
/// <para>
/// Skipped where there is no <c>node</c>, which on a developer's box is possible and on CI is not.
/// </para>
/// </remarks>
public class PlayitProxyWorkerTests
{
    private static string? Node()
    {
        try
        {
            var info = new ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            return process.ExitCode == 0 ? "node" : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The Worker only forwards the exact agent name and variant the app sends. If the app changed one
    /// and the Worker was not redeployed, every user would get 400 and nothing here would say why.
    /// </summary>
    [Theory]
    [InlineData("MC Server Launcher")]
    [InlineData("308943e8-faef-4835-a2ba-270351f72aa3")]
    public void TheWorkerAndTheAppAgreeOnWhatTheyIdentifyWith(string value)
    {
        var root = LocalizationTests.RepoRoot();
        var app = File.ReadAllText(Path.Combine(root, "McServerLauncher", "Services", "PlayitPartnerService.cs"));
        var worker = File.ReadAllText(Path.Combine(root, "playit-proxy", "worker.js"));

        Assert.Contains($"\"{value}\"", app);
        Assert.Contains($"\"{value}\"", worker);
    }

    [Fact]
    public async Task TheWorkerTestsPass()
    {
        if (Node() is not { } node) return;

        var folder = Path.Combine(LocalizationTests.RepoRoot(), "playit-proxy");
        var info = new ProcessStartInfo(node, "--test worker.test.mjs")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(info)!;
        // Both pipes are read at once so neither can fill and block the other.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "node --test did not finish in time");

        Assert.True(process.ExitCode == 0, "node --test worker.test.mjs:\n" + await stdout + await stderr);
    }
}
