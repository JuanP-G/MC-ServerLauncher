using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace McServerLauncher.Services;

/// <summary>
/// Comprueba qué puertos TCP están en uso en el equipo (por cualquier aplicación) y ayuda a
/// encontrar el siguiente puerto libre.
/// </summary>
public class PortService
{
    /// <summary>True si algún proceso del sistema está escuchando en ese puerto TCP.</summary>
    public bool IsPortInUse(int port)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listeners.Any(ep => ep.Port == port);
        }
        catch
        {
            // Si no se puede consultar, no bloqueamos por seguridad.
            return false;
        }
    }

    /// <summary>
    /// Primer puerto libre desde <paramref name="start"/> que no esté en uso por el sistema ni en
    /// el conjunto <paramref name="alsoAvoid"/> (p.ej. puertos de otros servidores registrados).
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

    // --- Identificar el PID que escucha en un puerto (para liberarlo) ---

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

    /// <summary>PID del proceso que está escuchando (LISTEN) en ese puerto TCP, o null.</summary>
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
