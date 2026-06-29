using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace McServerLauncher.Services;

/// <summary>
/// Checks which TCP ports are in use on the machine (by any application) and helps
/// find the next free port.
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
    /// the <paramref name="alsoAvoid"/> set (e.g. ports of other registered servers).
    /// </summary>
    public int FindFreePort(int start, ISet<int> alsoAvoid)
    {
        for (var port = start; port <= 65535; port++)
        {
            if (!alsoAvoid.Contains(port) && !IsPortInUse(port))
                return port;
        }
        return start;
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
