using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// The memory a process holds that belongs to it alone, as the system reports it.
/// </summary>
/// <remarks>
/// <para>
/// The working set that Task Manager shows counts memory shared with other processes (the JVM's
/// mapped class data, system libraries) and, on Windows, leaves out heap the JVM committed and has
/// not touched. What the machine really gives up for a server is its private memory, so that is what
/// the RAM card shows, and what the history keeps.
/// </para>
/// <para>
/// Windows: <c>PrivateMemorySize64</c>, the committed private bytes. Linux: the private pages in
/// <c>smaps_rollup</c>. Elsewhere, and whenever that cannot be read, the working set: a number a
/// little too high is better than none.
/// </para>
/// <para>
/// ZGC is the exception: on Windows its heap is mapped in a way private bytes do not count, so the
/// reading is far too low. The caller picks the working set for it (see <see cref="MemoryReadout"/>).
/// </para>
/// </remarks>
public static partial class ProcessMemory
{
    /// <summary>The process's private memory in MB, or null when the system will not say.</summary>
    /// <remarks>Call after <see cref="Process.Refresh"/>.</remarks>
    public static long? PrivateMb(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return process.PrivateMemorySize64 / (1024 * 1024);

            if (OperatingSystem.IsLinux()
                && ParsePrivateKb(File.ReadAllText($"/proc/{process.Id}/smaps_rollup")) is { } kb)
                return kb / 1024;
        }
        catch
        {
            // The process ended between the sample and this call, or /proc is out of reach.
        }
        return null;
    }

    /// <summary>The private kilobytes in the text of <c>smaps_rollup</c>, or null when it has none.</summary>
    public static long? ParsePrivateKb(string smapsRollup)
    {
        long total = 0;
        var found = false;
        foreach (Match m in PrivateLine().Matches(smapsRollup))
        {
            total += long.Parse(m.Groups["kb"].Value, CultureInfo.InvariantCulture);
            found = true;
        }
        return found ? total : null;
    }

    // Private_Clean:           4 kB, Private_Dirty: 120 kB, Private_Hugetlb: 0 kB
    [GeneratedRegex(@"^Private_(?:Clean|Dirty|Hugetlb):\s+(?<kb>\d+) kB", RegexOptions.Multiline)]
    private static partial Regex PrivateLine();
}

/// <summary>Which of a sample's numbers the RAM card shows.</summary>
public static class MemoryReadout
{
    /// <summary>
    /// The process's own memory in MB: private memory, or the working set when the collector is ZGC,
    /// whose heap private memory does not count.
    /// </summary>
    public static long OwnMb(ProcessStatsService.Stats sample, GcCollector collector) =>
        collector == GcCollector.Z || sample.PrivateMb <= 0 ? sample.RamMb : sample.PrivateMb;
}
