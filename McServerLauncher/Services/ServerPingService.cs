using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace McServerLauncher.Services;

/// <summary>Which leg of the trip a measurement covers.</summary>
public enum PingLeg
{
    /// <summary>Straight to the server on this machine. How long <i>it</i> takes to answer.</summary>
    Direct,

    /// <summary>Out to the playit edge and back again. Everything the direct leg covers, plus playit.</summary>
    Tunnel,
}

/// <summary>One measurement, or the reason there isn't one.</summary>
/// <param name="Leg">Which trip this timed.</param>
/// <param name="Milliseconds">Round trip, or null when nothing answered.</param>
/// <param name="Error">Why nothing answered. Null on success.</param>
public record PingResult(PingLeg Leg, int? Milliseconds, string? Error)
{
    public bool Answered => Milliseconds is not null;

    /// <summary>
    /// Nothing was measured, as opposed to something being measured and failing.
    /// </summary>
    /// <remarks>
    /// These two were the same thing until a panel said "your server answers here, but nothing
    /// answers through the tunnel" about a tunnel it had never once tried to reach — the address
    /// was known and the port was not, so the leg was skipped, and skipping came back looking
    /// exactly like silence. Accusing something on no evidence is worse than saying nothing.
    /// </remarks>
    public bool NotMeasured => Milliseconds is null && Error is null;

    /// <summary>A leg that was deliberately not attempted.</summary>
    public static PingResult Skipped(PingLeg leg) => new(leg, null, null);
}

/// <summary>
/// Asks a Minecraft server how long it takes to answer, from here.
/// </summary>
/// <remarks>
/// <para>
/// The protocol was already written in this house, facing the other way:
/// <see cref="WakeOnDemandListener"/> speaks it as a server so a client knocking on a stopped
/// server can be answered and the server woken. This is the same conversation from the other side,
/// and it deliberately reuses that class's helpers rather than carrying a second copy of the wire
/// format — two copies of a protocol is two things to keep in step.
/// </para>
/// <para>
/// <b>What this measures, and what it does not.</b> Both legs start and end at this machine. The
/// tunnel leg goes out to the playit edge and comes back here; it is not, and cannot be, the ping
/// of a player somewhere else, because the stretch between that player and playit never touches
/// this computer. Subtracting the two legs does answer the question worth asking — whether the
/// delay is the server or the tunnel — and that is all it answers.
/// </para>
/// </remarks>
public class ServerPingService
{
    /// <summary>
    /// Protocol number sent in the handshake.
    /// </summary>
    /// <remarks>
    /// -1 is the conventional "I am only asking, not joining". A real version number would make the
    /// server decide whether we are compatible and possibly refuse, and compatibility is not the
    /// question here — how fast it replies is.
    /// </remarks>
    private const int StatusOnlyProtocol = -1;

    /// <summary>How long to wait before calling it unanswered.</summary>
    /// <remarks>
    /// Generous for a handshake and a ping, which a healthy server finishes in milliseconds, and
    /// short enough that a dead address does not hold the panel for a noticeable pause.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>Whether there is anything here worth opening a socket for.</summary>
    /// <remarks>
    /// Its own function so it can be tested. Folded into <see cref="PingAsync"/> it was untestable
    /// in practice: without it a blank host simply fails in the socket instead, the result is
    /// "no answer" either way, and every test still passed with the check deleted. Being able to
    /// delete a guard without anything noticing is the definition of one that is not really there.
    /// </remarks>
    internal static bool IsAddressable(string? host, int port) =>
        !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;

    /// <summary>Times one round trip to <paramref name="host"/>.</summary>
    public async Task<PingResult> PingAsync(PingLeg leg, string host, int port,
        CancellationToken ct = default)
    {
        if (!IsAddressable(host, port))
            return new PingResult(leg, null, "sin dirección");

        try
        {
            using var client = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Timeout);

            await client.ConnectAsync(host, port, deadline.Token);
            client.ReceiveTimeout = client.SendTimeout = (int)Timeout.TotalMilliseconds;

            using var stream = client.GetStream();
            return Measure(leg, stream, host, port);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PingResult(leg, null, "sin respuesta");
        }
        catch (Exception ex)
        {
            return new PingResult(leg, null, ex.Message);
        }
    }

    /// <summary>
    /// The conversation itself, over any stream so it can be tested without a socket.
    /// </summary>
    /// <remarks>
    /// Only the 0x01 exchange is timed, not the connect and not the status JSON. That is what a
    /// Minecraft client puts on its own latency bar, so the number means the same thing here as it
    /// does in the game — and it leaves out DNS and the TCP handshake, which are paid once and
    /// would otherwise make the first reading of every session look like a problem.
    /// </remarks>
    internal static PingResult Measure(PingLeg leg, Stream stream, string host, int port)
    {
        // Handshake: protocol, the address we dialled, the port, and 1 for "status".
        WakeOnDemandListener.Send(stream, 0x00, w =>
        {
            WakeOnDemandListener.WriteVarInt(w, StatusOnlyProtocol);
            WakeOnDemandListener.WriteString(w, host);
            w.WriteByte((byte)(port >> 8));
            w.WriteByte((byte)(port & 0xFF));
            WakeOnDemandListener.WriteVarInt(w, 1);
        });

        // Status request, then its answer. The JSON is read and dropped: it has to be taken off the
        // wire before the ping, or the pong would be read out of the middle of it.
        WakeOnDemandListener.Send(stream, 0x00, _ => { });
        var status = WakeOnDemandListener.ReadPacket(stream);
        if (status is not { Id: 0x00 }) return new PingResult(leg, null, "respuesta inesperada");
        WakeOnDemandListener.ReadString(status.Value.Body);

        // The timed part. The payload is echoed back unchanged, so a reply carrying something else
        // is not our reply and the number would be meaningless.
        var sent = new byte[8];
        var stamp = Stopwatch.GetTimestamp();
        BitConverter.TryWriteBytes(sent, stamp);

        var clock = Stopwatch.StartNew();
        WakeOnDemandListener.Send(stream, 0x01, w => w.Write(sent, 0, sent.Length));

        var pong = WakeOnDemandListener.ReadPacket(stream);
        clock.Stop();

        if (pong is not { Id: 0x01 }) return new PingResult(leg, null, "sin respuesta");

        var echoed = new byte[8];
        if (pong.Value.Body.Read(echoed, 0, 8) != 8 || !echoed.AsSpan().SequenceEqual(sent))
            return new PingResult(leg, null, "respuesta inesperada");

        return new PingResult(leg, (int)clock.ElapsedMilliseconds, null);
    }

    /// <summary>
    /// What the two legs mean together.
    /// </summary>
    /// <remarks>
    /// The whole point of measuring twice. The tunnel leg contains the direct one, so the
    /// difference is what playit adds; a large direct reading means the server itself is struggling
    /// and no tunnel change would help.
    /// </remarks>
    public static PingVerdict Judge(PingResult direct, PingResult tunnel, int serverBudgetMs = 120)
    {
        if (!direct.Answered && !tunnel.Answered) return PingVerdict.Unknown;
        if (direct.Answered && direct.Milliseconds > serverBudgetMs) return PingVerdict.ServerSlow;

        // A leg that was never attempted says nothing about the tunnel. Only silence in answer to a
        // real attempt does.
        if (tunnel.NotMeasured) return direct.Answered ? PingVerdict.Fine : PingVerdict.Unknown;

        // A tunnel that stops answering while the server still does is the tunnel's problem, and it
        // is the case a "no data" verdict would hide behind a blank.
        if (direct.Answered && !tunnel.Answered) return PingVerdict.TunnelDown;

        if (direct.Answered && tunnel.Answered &&
            tunnel.Milliseconds - direct.Milliseconds > serverBudgetMs * 2)
            return PingVerdict.TunnelSlow;

        return PingVerdict.Fine;
    }
}

/// <summary>Who to blame, given both legs.</summary>
public enum PingVerdict
{
    /// <summary>Neither leg answered. Usually the server is simply stopped.</summary>
    Unknown,

    /// <summary>Both are normal.</summary>
    Fine,

    /// <summary>The server itself is slow to answer. A tunnel change would not help.</summary>
    ServerSlow,

    /// <summary>The server is fine; playit is adding the delay.</summary>
    TunnelSlow,

    /// <summary>The server answers here and the tunnel does not answer at all.</summary>
    TunnelDown,
}
