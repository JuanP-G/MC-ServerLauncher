using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>The machine's memory, as the memory budget and the performance advice need it.</summary>
public static partial class SystemMemory
{
    /// <summary>The physical memory available to this process, in MB. 0 if it cannot be read.</summary>
    /// <remarks>
    /// The runtime's own figure: the installed memory, or a container's limit when there is one, on
    /// every system the app runs on, without any platform call.
    /// </remarks>
    public static long TotalMb()
    {
        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The memory the system could give a program right now without swapping, in MB. 0 when it cannot
    /// be read, which makes the advice keep quiet about raising a server's memory.
    /// </summary>
    /// <remarks>
    /// Windows asks <c>GlobalMemoryStatusEx</c>; Linux reads <c>MemAvailable</c> from
    /// <c>/proc/meminfo</c>. Other systems have no cheap, honest answer here, so they have none.
    /// </remarks>
    public static long AvailableMb()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) ? (long)(status.AvailPhys / (1024 * 1024)) : 0;
            }
            if (OperatingSystem.IsLinux())
                return ParseMemAvailableMb(File.ReadAllText("/proc/meminfo"));
        }
        catch
        {
            // fall through: not knowing is a valid answer
        }
        return 0;
    }

    /// <summary><c>MemAvailable</c> in a <c>/proc/meminfo</c> text, in MB; 0 when it has none.</summary>
    public static long ParseMemAvailableMb(string meminfo)
    {
        var m = MemAvailable().Match(meminfo);
        return m.Success ? long.Parse(m.Groups["kb"].Value, CultureInfo.InvariantCulture) / 1024 : 0;
    }

    [GeneratedRegex(@"^MemAvailable:\s+(?<kb>\d+) kB", RegexOptions.Multiline)]
    private static partial Regex MemAvailable();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
