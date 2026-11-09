using System.Net;
using System.Net.Sockets;

namespace McServerLauncher.Services;

/// <summary>
/// A TCP listener that holds a stopped server's port and cannot be turned against the app: a cap on
/// connections, a thread of their own for each, a stall timeout and a deadline on every one.
/// </summary>
/// <remarks>
/// <para>
/// Both wake-on-demand listeners (<see cref="WakeOnDemandListener"/> for Java and
/// <see cref="NetherNetWakeListener"/> for Bedrock) were this code twice, and the bounds are the part
/// that must not drift between them: behind a Playit tunnel the socket is reachable by anyone on the
/// internet. What each of them says on the connection is the callback; how long it may live and how many
/// may be open at once is decided here.
/// </para>
/// <para>
/// <strong>A thread of its own for each connection, not one from the pool.</strong> The callback blocks
/// on its reads for up to the deadline, and sixteen slow clients used to hold sixteen pool threads for
/// fifteen seconds each; the pool grows slowly, so everything else in the app that awaits (requests,
/// backups, the other servers' listeners) queued behind them. The cap is what keeps the number of these
/// threads bounded.
/// </para>
/// <para>
/// <strong>The receive timeout is not the deadline.</strong> It limits how long ONE read may stall, and
/// every byte that arrives resets it, so a client that declares a large packet and dribbles a byte every
/// few seconds would keep its slot for ever; sixteen of those and the server could no longer be woken.
/// Closing the socket from a timer is what makes whichever read is blocked throw, without threading a
/// deadline through the parsing helpers (which stay static over <see cref="Stream"/>, and so testable
/// with a <see cref="MemoryStream"/>).
/// </para>
/// </remarks>
internal sealed class BoundedTcpListener : IDisposable
{
    /// <summary>How long a single read may stall. NOT a limit on the connection.</summary>
    internal const int ConnectionTimeoutMs = 5000;

    /// <summary>
    /// The ceiling on a connection's life, from accept to close: generous for a handshake and its ping,
    /// which a real client completes in milliseconds, and far too short to be worth camping on a slot.
    /// </summary>
    internal const int ConnectionDeadlineMs = 15000;

    /// <summary>Enough for a household; past this someone is not trying to play.</summary>
    internal const int MaxConcurrentConnections = 16;

    private readonly Action<NetworkStream> _serve;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _open;

    /// <param name="serve">Talks to one client. A thrown exception means a malformed client: it just
    /// gets no answer.</param>
    public BoundedTcpListener(Action<NetworkStream> serve) => _serve = serve;

    /// <summary>
    /// The deadline actually applied. Settable so a test can exercise it in seconds instead of waiting
    /// out the real one; production never touches it.
    /// </summary>
    internal TimeSpan ConnectionDeadline { get; set; } = TimeSpan.FromMilliseconds(ConnectionDeadlineMs);

    public bool IsListening => _listener is not null;

    /// <summary>
    /// Starts accepting on <paramref name="port"/>. Returns false when the port cannot be bound (a
    /// leftover server, another app): the caller carries on without wake-on-demand.
    /// </summary>
    public bool Start(int port)
    {
        Stop();

        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            _listener = listener;
            _cts = new CancellationTokenSource();

            _ = AcceptLoopAsync(listener, _cts.Token);
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    /// <summary>
    /// Releases the port. Must be called before the real server starts, or it finds the port busy — and
    /// the app would offer to kill the process holding it, which would be this one.
    /// </summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        _cts?.Dispose();
        _cts = null;

        try { _listener?.Dispose(); } catch { /* never started */ }
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }   // stopped, or the socket died

            if (Interlocked.Increment(ref _open) > MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _open);
                try { client.Close(); } catch { /* nothing to do */ }
                continue;
            }

            _ = Task.Factory.StartNew(() =>
            {
                try { Serve(client); }
                catch { /* malformed client: it just gets no answer */ }
                finally
                {
                    Interlocked.Decrement(ref _open);
                    try { client.Close(); } catch { /* already gone */ }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void Serve(TcpClient client)
    {
        client.ReceiveTimeout = client.SendTimeout = ConnectionTimeoutMs;

        using var deadline = new CancellationTokenSource(ConnectionDeadline);
        using var closeOnDeadline = deadline.Token.Register(() =>
        {
            try { client.Close(); } catch { /* already on its way out */ }
        });

        using var stream = client.GetStream();
        _serve(stream);
    }
}
