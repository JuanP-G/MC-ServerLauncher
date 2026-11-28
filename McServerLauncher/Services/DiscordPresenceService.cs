using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>
/// Keeps Discord's Rich Presence in step with what the caller says is running.
/// </summary>
/// <remarks>
/// <para>
/// Driven by <see cref="UpdateAsync"/> on a timer rather than by events: player joins in a burst
/// become one update, which keeps under Discord's rate limit, and a Discord opened or restarted
/// after the app is found at the next tick.
/// </para>
/// <para>
/// Discord not being installed or running is the normal state of many PCs, so nothing here is ever
/// shown as an error.
/// </para>
/// </remarks>
public sealed class DiscordPresenceService : IAsyncDisposable
{
    /// <summary>
    /// The "MC Server Launcher" application in Discord's developer portal; its name is what Discord
    /// writes after "Playing". Public by design, not a secret. Empty turns the feature off.
    /// </summary>
    internal const string ApplicationId = "1576208495324168192";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly Func<CancellationToken, Task<Stream?>> _connect;
    private readonly string _applicationId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Stream? _stream;
    private DiscordActivity? _shown;

    public DiscordPresenceService() : this(DiscordIpc.ConnectAsync, ApplicationId) { }

    internal DiscordPresenceService(Func<CancellationToken, Task<Stream?>> connect, string applicationId)
    {
        _connect = connect;
        _applicationId = applicationId;
    }

    /// <summary>Whether this build has a Discord application to speak as.</summary>
    public static bool IsAvailable => ApplicationId.Length > 0;

    /// <summary>
    /// Shows <paramref name="activity"/>, or nothing when null. Answers whether Discord is there.
    /// </summary>
    public async Task<bool> UpdateAsync(DiscordActivity? activity)
    {
        if (_applicationId.Length == 0) return false;

        await _gate.WaitAsync();
        try
        {
            using var cancel = new CancellationTokenSource(Timeout);
            if (_stream is null)
            {
                _shown = null;
                _stream = await _connect(cancel.Token);
                if (_stream is null) return false;

                await SendAsync(DiscordIpc.Handshake, DiscordIpc.HandshakeJson(_applicationId), cancel.Token);
                if (await AnswerAsync(cancel.Token) != "READY")
                    throw new IOException("Discord did not accept the handshake.");
            }

            // Sent again while unchanged: it is the only way to notice that Discord was closed.
            if (activity is not null || _shown is not null)
            {
                await SendAsync(DiscordIpc.Frame,
                    DiscordIpc.SetActivityJson(Environment.ProcessId, activity), cancel.Token);
                if (await AnswerAsync(cancel.Token) == "ERROR")
                    Safe.Note("Discord", "the presence was refused", new IOException("SET_ACTIVITY error"));
                _shown = activity;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                      or JsonException or ObjectDisposedException
                                      or UnauthorizedAccessException)
        {
            Safe.Note("Discord", "the connection was lost", ex);
            await DropAsync();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Hangs up, which also clears the presence. Safe to call twice.</summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { await DropAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private async Task DropAsync()
    {
        var stream = _stream;
        _stream = null;
        _shown = null;
        if (stream is null) return;
        try { await stream.DisposeAsync(); }
        catch (IOException) { /* already broken, which is why we are here */ }
    }

    private async Task SendAsync(int opcode, string json, CancellationToken ct)
    {
        await _stream!.WriteAsync(DiscordIpc.Encode(opcode, json), ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>The event of the next answer, replying to pings on the way.</summary>
    private async Task<string?> AnswerAsync(CancellationToken ct)
    {
        while (true)
        {
            var (opcode, json) = await DiscordIpc.ReadAsync(_stream!, ct);
            if (opcode == DiscordIpc.Ping) await SendAsync(DiscordIpc.Pong, json, ct);
            else if (opcode == DiscordIpc.Frame) return DiscordIpc.EventOf(json);
            else if (opcode == DiscordIpc.Close) throw new IOException("Discord closed the connection.");
        }
    }
}
