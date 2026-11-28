using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

public class DiscordPresenceTests
{
    private static readonly DateTime Start = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static DiscordServer Server(string name = "Survival", int? players = 3, DateTime? started = null) =>
        new(name, ServerType.Paper, "1.21.8", players, 20, started ?? Start);

    [Fact]
    public void NothingRunning_ShowsNothing() => Assert.Null(DiscordActivity.For([]));

    [Fact]
    public void OneServer_ShowsItsNameTypeVersionAndPlayers()
    {
        var card = DiscordActivity.For([Server()])!;

        Assert.Equal("Survival", card.Details);
        Assert.Equal(Localizer.Format("Discord_OneStateFmt", "Paper 1.21.8", 3, 20), card.State);
        Assert.Equal("Paper", card.SmallText);
        Assert.EndsWith("/TypeLogos/paper.png", card.SmallImage);
        Assert.Equal(new DateTimeOffset(Start).ToUnixTimeSeconds(), card.StartUnixSeconds);
    }

    [Fact]
    public void SeveralServers_ShowOnlyHowMany_AndCountFromTheOldest()
    {
        var card = DiscordActivity.For(
            [Server("Secret one", 3, Start.AddHours(1)), Server("Secret two", 4)])!;

        Assert.Equal(Localizer.Format("Discord_ServersFmt", 2), card.Details);
        Assert.Equal(Localizer.Format("Discord_PlayersFmt", 7), card.State);
        Assert.Null(card.SmallImage);
        Assert.Equal(new DateTimeOffset(Start).ToUnixTimeSeconds(), card.StartUnixSeconds);
    }

    [Fact]
    public void AnUnknownPlayerCount_IsLeftOut_NotReportedAsZero()
    {
        Assert.Equal("Paper 1.21.8", DiscordActivity.For([Server(players: null)])!.State);
        Assert.Null(DiscordActivity.For([Server(players: null), Server()])!.State);
    }

    [Fact]
    public void Lines_StayWithinWhatDiscordAccepts()
    {
        Assert.Equal("Paper", DiscordActivity.For([Server("a")])!.Details);

        var details = DiscordActivity.For([Server(new string('x', 127) + "\U0001F600")])!.Details;
        Assert.Equal(127, details.Length);
        Assert.False(char.IsSurrogate(details[^1]));
    }

    [Fact]
    public void EveryTypeLogoTheCardPointsAt_IsInTheRepository()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "McServerLauncher.sln")))
            folder = folder.Parent;
        Assert.NotNull(folder);

        const string marker = "/main/";
        foreach (var type in Enum.GetValues<ServerType>())
        {
            var url = DiscordActivity.For([Server() with { Type = type }])!.SmallImage!;
            foreach (var address in new[] { url, DiscordActivity.LargeImage })
                Assert.True(File.Exists(Path.Combine(folder.FullName,
                    address[(address.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..])), address);
        }
    }

    [Fact]
    public void AFrame_IsOpcodeAndLengthLittleEndian_ThenTheJson()
    {
        var frame = DiscordIpc.Encode(DiscordIpc.Frame, "{\"a\":\"é\"}");

        Assert.Equal(DiscordIpc.Frame, BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal(frame.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal("{\"a\":\"é\"}", Encoding.UTF8.GetString(frame, 8, frame.Length - 8));
    }

    [Fact]
    public void SocketPaths_FollowDiscordsOrder_AndLookInsideTheSandboxes()
    {
        var env = new Dictionary<string, string> { ["TMPDIR"] = "/var/t/", ["TMP"] = "/other" };

        Assert.Equal(
            ["/var/t/discord-ipc-1", "/var/t/app/com.discordapp.Discord/discord-ipc-1",
             "/var/t/snap.discord/discord-ipc-1"],
            DiscordIpc.SocketPaths(1, name => env.GetValueOrDefault(name)));
        Assert.Equal("/tmp/discord-ipc-0", DiscordIpc.SocketPaths(0, _ => null).First());
    }

    [Fact]
    public async Task WithoutDiscord_ItSaysSo_AndTriesAgainNextTime()
    {
        var attempts = 0;
        await using var service = new DiscordPresenceService(_ =>
        {
            attempts++;
            return Task.FromResult<Stream?>(null);
        }, "1");

        Assert.False(await service.UpdateAsync(null));
        Assert.False(await service.UpdateAsync(null));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ItShakesHands_Publishes_Clears_AndThenStaysQuiet()
    {
        var discord = new FakeDiscord();
        await using var service = new DiscordPresenceService(_ => Task.FromResult<Stream?>(discord), "42");
        var card = DiscordActivity.For([Server()]);

        Assert.True(await service.UpdateAsync(null));   // connected, nothing to say
        Assert.True(await service.UpdateAsync(card));
        Assert.True(await service.UpdateAsync(null));   // the server stopped
        Assert.True(await service.UpdateAsync(null));

        Assert.Equal(3, discord.Sent.Count);
        Assert.Equal("42", discord.Sent[0].GetProperty("client_id").GetString());
        var shown = discord.Sent[1].GetProperty("args").GetProperty("activity");
        Assert.Equal("Survival", shown.GetProperty("details").GetString());
        Assert.False(discord.Sent[2].GetProperty("args").TryGetProperty("activity", out _));
    }

    [Fact]
    public async Task WhenDiscordGoesAway_ItConnectsAgain()
    {
        var attempts = 0;
        await using var service = new DiscordPresenceService(_ =>
        {
            attempts++;
            return Task.FromResult<Stream?>(new FakeDiscord { Answers = 1 });
        }, "1");
        var card = DiscordActivity.For([Server()]);

        Assert.False(await service.UpdateAsync(card));   // answers the handshake, then hangs up
        Assert.False(await service.UpdateAsync(card));
        Assert.Equal(2, attempts);
    }

    /// <summary>The other end of the pipe: records what arrives and answers each frame.</summary>
    private sealed class FakeDiscord : Stream
    {
        private readonly MemoryStream _answers = new();
        private long _read;

        public int Answers { get; set; } = int.MaxValue;
        public List<JsonElement> Sent { get; } = new();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Sent.Add(JsonDocument.Parse(buffer[8..]).RootElement.Clone());
            if (Sent.Count > Answers) return ValueTask.CompletedTask;
            var evt = Sent.Count == 1 ? "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}" : "{\"evt\":null}";
            var position = _answers.Length;
            _answers.Position = position;
            _answers.Write(DiscordIpc.Encode(DiscordIpc.Frame, evt));
            return ValueTask.CompletedTask;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            _answers.Position = _read;
            var n = _answers.Read(buffer.Span);
            _read += n;
            return ValueTask.FromResult(n);
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
