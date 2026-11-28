using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McServerLauncher.Services;

/// <summary>
/// Discord's local IPC: a named pipe on Windows, a Unix socket elsewhere. A frame is an opcode and a
/// length (32-bit, little-endian) followed by that much JSON.
/// </summary>
/// <remarks>
/// Written here rather than taken from a package: it is a handshake and one command, and the
/// .NET named pipe on Linux does not look where Discord puts its socket.
/// </remarks>
internal static class DiscordIpc
{
    internal const int Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4;

    /// <summary>No answer to a presence update comes near this; a bigger length is a broken stream.</summary>
    private const int MaxPayload = 64 * 1024;

    internal static byte[] Encode(int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        return frame;
    }

    internal static async Task<(int Opcode, string Json)> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > MaxPayload) throw new IOException($"Discord frame of {length} bytes.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (opcode, Encoding.UTF8.GetString(body));
    }

    internal static string HandshakeJson(string applicationId) =>
        new JsonObject { ["v"] = 1, ["client_id"] = applicationId }.ToJsonString();

    /// <summary>SET_ACTIVITY; without an activity it clears the presence.</summary>
    internal static string SetActivityJson(int pid, DiscordActivity? activity)
    {
        var args = new JsonObject { ["pid"] = pid };
        if (activity is not null)
        {
            var assets = new JsonObject
            {
                ["large_image"] = DiscordActivity.LargeImage,
                ["large_text"] = "MC Server Launcher",
            };
            if (activity.SmallImage is not null)
            {
                assets["small_image"] = activity.SmallImage;
                assets["small_text"] = activity.SmallText;
            }

            var card = new JsonObject
            {
                ["details"] = activity.Details,
                ["timestamps"] = new JsonObject { ["start"] = activity.StartUnixSeconds },
                ["assets"] = assets,
            };
            if (activity.State is not null) card["state"] = activity.State;
            args["activity"] = card;
        }

        return new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = args,
            ["nonce"] = Guid.NewGuid().ToString(),
        }.ToJsonString();
    }

    /// <summary>The event name of an answer ("READY", "ERROR"), or null.</summary>
    internal static string? EventOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("evt", out var evt)
            && evt.ValueKind == JsonValueKind.String
                ? evt.GetString()
                : null;
    }

    /// <summary>The pipe to a running Discord, or null when there is none — the ordinary case.</summary>
    internal static async Task<Stream?> ConnectAsync(CancellationToken ct)
    {
        // Listing the pipes does not open any; asking whether one of them exists can.
        var pipes = OperatingSystem.IsWindows()
            ? Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).ToHashSet()
            : null;

        for (var i = 0; i < 10; i++)
        {
            if (OperatingSystem.IsWindows())
            {
                // Asking first: connecting to a pipe that is not there waits out the whole timeout.
                if (!pipes!.Contains($"discord-ipc-{i}")) continue;
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(500, ct);
                    return pipe;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    await pipe.DisposeAsync();
                }
                continue;
            }

            foreach (var path in SocketPaths(i, Environment.GetEnvironmentVariable))
            {
                if (!File.Exists(path)) continue;
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException)
                {
                    socket.Dispose();   // a socket left behind by a Discord that is gone
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Where Discord may have put socket number <paramref name="index"/> on Linux and macOS,
    /// including inside the Flatpak and Snap sandboxes.
    /// </summary>
    internal static IEnumerable<string> SocketPaths(int index, Func<string, string?> environment)
    {
        var root = new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" }
            .Select(environment)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "/tmp";
        root = root.TrimEnd('/');
        foreach (var sub in new[] { "", "app/com.discordapp.Discord/", "snap.discord/" })
            yield return $"{root}/{sub}discord-ipc-{index}";
    }
}
