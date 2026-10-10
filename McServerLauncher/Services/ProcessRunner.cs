using System.Diagnostics;

namespace McServerLauncher.Services;

/// <summary>
/// Runs a short external command and collects what it printed, within a deadline that holds.
/// </summary>
/// <remarks>
/// <para>
/// The helpers this replaces read standard output to its end and only then called
/// <c>WaitForExit(5000)</c>. Reading to the end already waits for the process to finish, so the
/// five seconds never applied: a <c>systemctl start</c> waiting on a polkit prompt, or anything else
/// that hangs, held its caller for as long as it liked. And reading one stream to the end before the
/// other can deadlock a process that fills the second one's buffer first.
/// </para>
/// <para>
/// Here both streams are read at once and the deadline is real: past it the process tree is killed
/// and the result says so with an exit code of -1.
/// </para>
/// </remarks>
public static class ProcessRunner
{
    /// <summary>What a command left behind. <c>ExitCode</c> is -1 when it could not run or ran out of time.</summary>
    public readonly record struct Result(int ExitCode, string Output, string Error);

    /// <summary>Runs <paramref name="file"/> with <paramref name="arguments"/>. Never throws.</summary>
    public static Result Run(string file, IEnumerable<string> arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);

            using var p = Process.Start(psi);
            if (p is null) return new Result(-1, string.Empty, string.Empty);

            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new Result(-1, string.Empty, string.Empty);
            }

            return new Result(p.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch
        {
            return new Result(-1, string.Empty, string.Empty);
        }
    }
}
