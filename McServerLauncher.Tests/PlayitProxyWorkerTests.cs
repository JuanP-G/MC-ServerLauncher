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

    /// <summary>
    /// SECURITY.md and the proxy's README say it stores nothing and logs no request data. That has to
    /// be something the files enforce: a Worker created on Cloudflare has Workers Logs on by default
    /// (client IP and URL of every attempt, kept seven days), and one <c>console.log(body)</c> would put
    /// the setup code and the key in them.
    /// </summary>
    [Fact]
    public void TheProxyKeepsItsPromiseToStoreAndLogNothing()
    {
        var folder = Path.Combine(LocalizationTests.RepoRoot(), "playit-proxy");
        var config = File.ReadAllText(Path.Combine(folder, "wrangler.toml"));
        var worker = File.ReadAllText(Path.Combine(folder, "worker.js"));

        var section = System.Text.RegularExpressions.Regex.Match(
            config, @"^\[observability\]\s*\r?\n(?:#.*\r?\n)*\s*enabled\s*=\s*false\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(section.Success, "wrangler.toml must switch Workers Logs off ([observability] enabled = false)");

        // Only a fixed message plus the caught exception may be logged, never anything from the request.
        foreach (System.Text.RegularExpressions.Match call in
                 System.Text.RegularExpressions.Regex.Matches(worker, @"console\.\w+\([^)]*\)"))
        {
            Assert.Matches(@"^console\.error\(""[^""]*"",\s*e\)$", call.Value);
        }
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
