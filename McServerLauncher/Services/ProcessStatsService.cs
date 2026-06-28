using System.Diagnostics;

namespace McServerLauncher.Services;

/// <summary>
/// Calcula uso de CPU (%) y RAM (MB) de un proceso, muestreando el tiempo de CPU
/// entre llamadas consecutivas.
/// </summary>
public class ProcessStatsService
{
    private TimeSpan _lastCpuTime;
    private DateTime _lastSample = DateTime.UtcNow;

    public record Stats(double CpuPercent, long RamMb, TimeSpan Uptime);

    /// <summary>Reinicia el muestreo (al cambiar de proceso/servidor).</summary>
    public void Reset()
    {
        _lastCpuTime = TimeSpan.Zero;
        _lastSample = DateTime.UtcNow;
    }

    /// <summary>
    /// Toma una muestra de estadísticas del proceso. Devuelve null si el proceso ya no existe.
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
