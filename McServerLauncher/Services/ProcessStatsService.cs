using System.Diagnostics;

namespace McServerLauncher.Services;

/// <summary>
/// Computes CPU usage (%) and RAM (MB) of a process, sampling the CPU time
/// between consecutive calls.
/// </summary>
public class ProcessStatsService
{
    private TimeSpan _lastCpuTime;
    private DateTime _lastSample = DateTime.UtcNow;

    public record Stats(double CpuPercent, long RamMb, TimeSpan Uptime);

    /// <summary>Resets the sampling (when switching process/server).</summary>
    public void Reset()
    {
        _lastCpuTime = TimeSpan.Zero;
        _lastSample = DateTime.UtcNow;
    }

    /// <summary>
    /// Takes a sample of the process stats. Returns null if the process no longer exists.
    /// </summary>
    public Stats? Sample(Process? process)
    {
        if (process is null)
            return null;

        try
        {
            process.Refresh();
            if (process.HasExited)
                return null;

            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;

            var wallMs = (now - _lastSample).TotalMilliseconds;
            var cpuMs = (cpuTime - _lastCpuTime).TotalMilliseconds;

            double cpuPercent = 0;
            if (wallMs > 0 && _lastCpuTime > TimeSpan.Zero)
                cpuPercent = cpuMs / (wallMs * Environment.ProcessorCount) * 100.0;

            _lastCpuTime = cpuTime;
            _lastSample = now;

            var ramMb = process.WorkingSet64 / (1024 * 1024);
            var uptime = now - process.StartTime.ToUniversalTime();

            return new Stats(Math.Clamp(cpuPercent, 0, 100), ramMb, uptime);
        }
        catch
        {
            return null;
        }
    }
}
