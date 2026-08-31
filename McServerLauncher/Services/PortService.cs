using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Checks which ports are in use on the machine (by any application) and helps find the next free
/// one. TCP and UDP are asked separately because they are separate namespaces.
/// </summary>
public class PortService
{
    /// <summary>True if any system process is listening on that TCP port.</summary>
    public bool IsPortInUse(int port)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listeners.Any(ep => ep.Port == port);
        }
        catch
        {
            // If it can't be queried, don't block to be safe.
            return false;
        }
    }

    /// <summary>
    /// First free port from <paramref name="start"/> that is not in use by the system nor in
    /// the <paramref name="alsoAvoid"/> set (e.g. ports of other registered servers). Returns
    /// null if every port up to 65535 is taken, so the caller can react instead of silently
    /// getting a busy port back. The system's listener table is snapshotted once for the whole
    /// scan (instead of re-queried per port).
    /// </summary>
    public int? FindFreePort(int start, ISet<int> alsoAvoid)
    {
        HashSet<int>? inUse = null;
        try
        {
            inUse = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Select(ep => ep.Port)
                .ToHashSet();
        }
        catch
        {
            // If the table can't be queried, fall back to only avoiding the known ports.
        }

        for (var port = start; port <= 65535; port++)
        {
            if (!alsoAvoid.Contains(port) && inUse?.Contains(port) != true)
                return port;
        }
        return null;
    }

    // --- UDP ---
    // A separate table, and asking the wrong one is not a near miss. Minecraft Java is TCP and
    // Bedrock is UDP, and a port can be busy on one while free on the other, so checking Bedrock's
    // port against the TCP table would hand out a port something else is already using — and the
    // clash would only show up as a Geyser that silently fails to bind.

    /// <summary>True if any system process is listening on that UDP port.</summary>
    public bool IsUdpPortInUse(int port)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
            return listeners.Any(ep => ep.Port == port);
        }
        catch
        {
            // Same choice as the TCP one: if it can't be queried, don't block.
            return false;
        }
    }

    /// <summary>
    /// First free UDP port from <paramref name="start"/>, skipping <paramref name="alsoAvoid"/>.
    /// Null when everything up to 65535 is taken.
    /// </summary>
    public int? FindFreeUdpPort(int start, ISet<int> alsoAvoid)
    {
        HashSet<int>? inUse = null;
        try
        {
            inUse = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                .Select(ep => ep.Port)
                .ToHashSet();
        }
        catch
        {
            // As above: fall back to only avoiding the ports we already know about.
        }

        for (var port = start; port <= 65535; port++)
        {
            if (!alsoAvoid.Contains(port) && inUse?.Contains(port) != true)
                return port;
        }
        return null;
    }

    // --- Identify the PID listening on a port (to free it) ---

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, int reserved);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    /// <summary>PID of the process listening (LISTEN) on that TCP port, or null.</summary>
    public int? GetListeningPid(int port)
    {
        if (OperatingSystem.IsWindows()) return GetListeningPidWindows(port);
        if (OperatingSystem.IsLinux()) return GetListeningPidLinux(port);
        return null;
    }

    /// <summary>Linux: parse 'ss -ltnp' to find the PID listening on the port.</summary>
    private static int? GetListeningPidLinux(int port)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ss",
                Arguments = "-ltnpH",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);

            foreach (var line in output.Split('\n'))
            {
                // The listening line contains the local address ":<port>" and users:(("name",pid=NNN,fd=N)).
                if (!Regex.IsMatch(line, $@":{port}\b")) continue;
                var m = Regex.Match(line, @"pid=(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pid))
                    return pid;
            }
        }
        catch
        {
            // ss not available or no permission: we just can't identify the PID.
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static int? GetListeningPidWindows(int port)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return null;

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                var localPort = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort & 0xFF00) >> 8));
                if (localPort == port)
                    return (int)row.OwningPid;
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return null;
    }
}
