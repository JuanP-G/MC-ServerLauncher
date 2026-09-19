using System.IO.Compression;
using System.Text;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The player history: what counts as something a player did, what is kept, for how long, and what
/// is never kept at all.
/// </summary>
/// <remarks>
/// <para>
/// It has to stay small on a server with many players, so the limits are held down here rather than
/// trusted. And it must never keep an IP address: that is checked against the bytes on disk, not
/// against a comment promising it.
/// </para>
/// </remarks>
public class PlayerHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-history-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private PlayerHistorySettings _settings = new();

    public PlayerHistoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private PlayerHistoryStore Store(string name = "server") =>
        new(Path.Combine(_root, name), () => _settings.Clamped(), () => _now);

    private static readonly IReadOnlySet<string> Nobody = new HashSet<string>();

    private static PlayerEvent Join(string p) => new(PlayerEventKind.Join, p, null);
    private static PlayerEvent Leave(string p) => new(PlayerEventKind.Leave, p, null);
    private static PlayerEvent Chat(string p, string m) => new(PlayerEventKind.Chat, p, m);

    private string AllBytesOnDisk() => string.Concat(
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(File.ReadAllText));

    // --- Reading a line ---

    [Theory]
    [InlineData("[21:10:03] [Server thread/INFO]: Alice joined the game", PlayerEventKind.Join, "Alice", null)]
    [InlineData("[21:10:03 INFO]: Alice left the game", PlayerEventKind.Leave, "Alice", null)]
    [InlineData("[21:10:03] [Server thread/INFO]: <Alice> hola", PlayerEventKind.Chat, "Alice", "hola")]
    [InlineData("[21:10:03] [Server thread/INFO]: [Not Secure] <Alice> hola", PlayerEventKind.Chat, "Alice", "hola")]
    [InlineData("[21:10:03] [Server thread/INFO]: Alice was slain by Zombie", PlayerEventKind.Death, "Alice", "Alice was slain by Zombie")]
    [InlineData("[21:10:03] [Server thread/INFO]: Alice has made the advancement [Stone Age]", PlayerEventKind.Advancement, "Alice", "Stone Age")]
    [InlineData("[21:10:03] [Server thread/INFO]: Alice has completed the challenge [Monsters Hunted]", PlayerEventKind.Advancement, "Alice", "Monsters Hunted")]
    public void EachThingAPlayerDoesIsRecognised(string line, PlayerEventKind kind, string player, string? text)
    {
        Assert.Equal(new PlayerEvent(kind, player, text), PlayerEventParser.Parse(line, Nobody));
    }

    [Theory]
    [InlineData("[21:10:03] [Server thread/INFO]: <Bob> Alice joined the game")]              // quoting it
    [InlineData("[21:10:03] [Server thread/INFO]: <Bob> Alice has made the advancement [X]")]
    [InlineData("[21:10:03] [Server thread/INFO]: [Server] Alice joined the game")]           // say
    public void QuotingSomethingIsNotDoingIt(string line)
    {
        var e = PlayerEventParser.Parse(line, Nobody);
        Assert.True(e is null || e.Kind == PlayerEventKind.Chat);
        Assert.NotEqual("Alice", e?.Player);
    }

    [Fact]
    public void TheServerTalkingIsNobodysChat()
    {
        Assert.Null(PlayerEventParser.Parse("[21:10:03] [Server thread/INFO]: [Server] reinicio en 5", Nobody));
    }

    [Fact]
    public void TheUuidLineIsRead()
    {
        Assert.Equal(("Alice", "069a79f4-44e9-4726-a5be-fca90e38aaf5"), PlayerEventParser.UuidOf(
            "[21:10:02] [User Authenticator #1/INFO]: UUID of player Alice is 069A79F4-44E9-4726-A5BE-FCA90E38AAF5"));
    }

    [Fact]
    public void TheLoginLineWithTheIpIsNeverAnEvent()
    {
        Assert.Null(PlayerEventParser.Parse(
            "[21:10:03] [Server thread/INFO]: Alice[/203.0.113.7:51234] logged in with entity id 12 at (0.5, 64.0, 0.5)", Nobody));
    }

    // --- Recording ---

    [Fact]
    public void WhatIsRecordedReadsBackNewestFirst()
    {
        var store = Store();
        store.Record(Join("Alice"), _now.AddMinutes(-30));
        store.Record(Chat("Alice", "hola"), _now.AddMinutes(-20));
        store.Record(Leave("Alice"), _now.AddMinutes(-10));

        var events = store.Events("alice");
        Assert.Equal(new[] { PlayerEventKind.Leave, PlayerEventKind.Chat, PlayerEventKind.Join }, events.Select(e => e.Kind));
        Assert.Equal("hola", events[1].Text);

        var rec = store.Get("Alice")!;
        Assert.Equal(1, rec.Sessions);
        Assert.Equal(20 * 60, rec.SecondsPlayed);
        Assert.Equal(1, rec.Messages);
        Assert.Equal(_now.AddMinutes(-10), rec.LastSeen);
        Assert.Equal(_now.AddMinutes(-30), rec.FirstSeen);
    }

    [Fact]
    public void AnIpAddressNeverReachesTheDisk()
    {
        // The whole live path, fed the real login sequence. Whatever it keeps, it keeps without the IP.
        var store = Store();
        var lines = new[]
        {
            "[21:10:02] [User Authenticator #1/INFO]: UUID of player Alice is 069a79f4-44e9-4726-a5be-fca90e38aaf5",
            "[21:10:03] [Server thread/INFO]: Alice[/203.0.113.7:51234] logged in with entity id 12 at (0.5, 64.0, 0.5)",
            "[21:10:03] [Server thread/INFO]: Alice joined the game",
            "[21:12:00] [Server thread/INFO]: <Alice> mi ip es otra cosa",
            "[21:15:00] [Server thread/INFO]: Alice lost connection: Disconnected",
            "[21:15:00] [Server thread/INFO]: Alice left the game",
        };
        foreach (var line in lines)
        {
            if (PlayerEventParser.UuidOf(line) is { } u) store.RecordUuid(u.Player, u.Uuid);
            else if (PlayerEventParser.Parse(line, Nobody) is { } e) store.Record(e, _now);
        }
        store.Flush();

        var disk = AllBytesOnDisk();
        Assert.Contains("069a79f4", disk);           // it did record things
        Assert.DoesNotContain("203.0.113.7", disk);
        Assert.DoesNotContain("51234", disk);
    }

    [Fact]
    public void TheLimitKeepsTheNewestEvents()
    {
        _settings = new PlayerHistorySettings { MaxEventsPerPlayer = 50 };
        var store = Store();
        for (var i = 0; i < 200; i++) store.Record(Chat("Alice", "m" + i), _now.AddSeconds(-200 + i));

        var events = store.Events("Alice");
        Assert.InRange(events.Count, 50, 50 * 5 / 4);            // compacted in blocks, never far over
        Assert.Equal("m199", events[0].Text);                     // the newest is always there

        store.Prune();
        Assert.Equal(50, store.Events("Alice").Count);
        Assert.Equal("m150", store.Events("Alice")[^1].Text);
    }

    [Fact]
    public void OldEventsAndLongGonePlayersAreDropped()
    {
        _settings = new PlayerHistorySettings { RetentionDays = 30 };
        var store = Store();
        store.Record(Join("Old"), _now.AddDays(-20));
        store.Record(Leave("Old"), _now.AddDays(-20).AddHours(1));
        store.Record(Join("Alice"), _now.AddDays(-20));
        store.Record(Chat("Alice", "reciente"), _now.AddDays(-1));

        _now = _now.AddDays(15);   // Old was last seen 35 days ago; Alice's join is 35 days old
        store.Prune();

        Assert.Null(store.Get("Old"));
        Assert.Empty(store.Events("Old"));
        Assert.Equal(new[] { "reciente" }, store.Events("Alice").Select(e => e.Text));
    }

    [Fact]
    public void SwitchedOffNothingIsWritten()
    {
        _settings = new PlayerHistorySettings { Enabled = false };
        var store = Store();
        store.Record(Join("Alice"), _now);
        store.RecordUuid("Alice", "069a79f4-44e9-4726-a5be-fca90e38aaf5");
        store.Flush();

        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void WithoutChatTheConnectionsAreStillKept()
    {
        _settings = new PlayerHistorySettings { RecordChat = false };
        var store = Store();
        store.Record(Join("Alice"), _now);
        store.Record(Chat("Alice", "secreto"), _now);

        Assert.Equal(new[] { PlayerEventKind.Join }, store.Events("Alice").Select(e => e.Kind));
        Assert.DoesNotContain("secreto", AllBytesOnDisk());
    }

    [Fact]
    public void AStopEndsTheSessionsStillOpen()
    {
        var store = Store();
        store.Record(Join("Alice"), _now.AddHours(-2));

        store.CloseOpenSessions(_now);

        var rec = store.Get("Alice")!;
        Assert.Null(rec.OpenSince);
        Assert.Equal(2 * 3600, rec.SecondsPlayed);
        Assert.Equal(_now, rec.LastLeave);
    }

    [Fact]
    public void AJoinWithoutALeaveCountsOnlyUpToTheLastThingThePlayerDid()
    {
        // A crash in between: the leave never came. Counting up to the next join would add all the
        // hours the server was down.
        var store = Store();
        store.Record(Join("Alice"), _now.AddHours(-10));
        store.Record(Chat("Alice", "adiós"), _now.AddHours(-9));
        store.Record(Join("Alice"), _now.AddHours(-1));

        var rec = store.Get("Alice")!;
        Assert.Equal(3600, rec.SecondsPlayed);
        Assert.Equal(2, rec.Sessions);
    }

    [Fact]
    public void TheIndexSurvivesClosingAndOpeningAgain()
    {
        var store = Store();
        store.Record(Join("Alice"), _now.AddMinutes(-5));
        store.RecordUuid("Alice", "069a79f4-44e9-4726-a5be-fca90e38aaf5");
        store.Dispose();

        var again = Store();
        Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5", again.Get("alice")!.Uuid);
        Assert.Single(again.Events("Alice"));
    }

    [Fact]
    public void ForgettingAPlayerForgetsOnlyThem()
    {
        var store = Store();
        store.Record(Chat("Alice", "a"), _now);
        store.Record(Chat("Bob", "b"), _now);

        store.Clear("Alice");

        Assert.Null(store.Get("Alice"));
        Assert.Empty(store.Events("Alice"));
        Assert.Single(store.Events("Bob"));
    }

    [Fact]
    public void ANameCanNeverBecomeAPath()
    {
        var store = Store();
        store.Record(new PlayerEvent(PlayerEventKind.Chat, "../../evil", "x"), _now);
        store.Clear("..\\..\\x");

        Assert.Empty(store.Players());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(_root)!, "evil*"));
    }

    [Fact]
    public void ADamagedLineIsSkippedNotFatal()
    {
        var store = Store();
        store.Record(Chat("Alice", "bien"), _now);
        File.AppendAllText(Path.Combine(_root, "server", "events", "alice.jsonl"), "{\"t\":12,\"k\":\n");

        Assert.Equal(new[] { "bien" }, store.Events("Alice").Select(e => e.Text));
    }

    // --- Importing the server's old logs ---

    private string ServerWithLogs(params (string File, string[] Lines)[] logs)
    {
        var server = Path.Combine(_root, "srv-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(server, "logs");
        Directory.CreateDirectory(dir);
        foreach (var (file, lines) in logs)
        {
            var text = string.Join("\n", lines) + "\n";
            var path = Path.Combine(dir, file);
            if (file.EndsWith(".gz", StringComparison.Ordinal))
            {
                using var fs = File.Create(path);
                using var gz = new GZipStream(fs, CompressionLevel.Fastest);
                gz.Write(Encoding.UTF8.GetBytes(text));
            }
            else
            {
                File.WriteAllText(path, text);
            }
        }
        return server;
    }

    private static DateTime Local(int y, int mo, int d, int h, int mi, int s) =>
        new DateTime(y, mo, d, h, mi, s, DateTimeKind.Local).ToUniversalTime();

    [Fact]
    public void OldLogsFillTheHistoryWithTheirOwnDates()
    {
        _now = Local(2026, 9, 19, 12, 0, 0);
        var server = ServerWithLogs(("2026-09-10-1.log.gz", new[]
        {
            "[10:00:00] [Server thread/INFO]: Starting minecraft server version 26.2",
            "[10:05:00] [Server thread/INFO]: Alice joined the game",
            "[10:06:00] [Server thread/INFO]: <Alice> hola",
            "[11:05:00] [Server thread/INFO]: Alice left the game",
        }));
        var store = Store();

        PlayerLogImporter.ImportOnce(server, store, serverRunning: false);

        var rec = store.Get("Alice")!;
        Assert.Equal(Local(2026, 9, 10, 10, 5, 0), rec.FirstSeen);
        Assert.Equal(Local(2026, 9, 10, 11, 5, 0), rec.LastLeave);
        Assert.Equal(3600, rec.SecondsPlayed);
        Assert.Equal(3, store.Events("Alice").Count);
        Assert.True(store.Imported);
    }

    [Fact]
    public void ALogThatCrossesMidnightMovesToTheNextDay()
    {
        _now = Local(2026, 9, 19, 12, 0, 0);
        var server = ServerWithLogs(("2026-09-10-1.log.gz", new[]
        {
            "[23:50:00] [Server thread/INFO]: Alice joined the game",
            "[00:10:00] [Server thread/INFO]: Alice left the game",
        }));
        var store = Store();

        PlayerLogImporter.ImportOnce(server, store, serverRunning: false);

        Assert.Equal(Local(2026, 9, 11, 0, 10, 0), store.Get("Alice")!.LastLeave);
        Assert.Equal(20 * 60, store.Get("Alice")!.SecondsPlayed);
    }

    [Fact]
    public void LatestLogIsDatedBackFromItsLastWrite()
    {
        _now = Local(2026, 9, 19, 12, 0, 0);
        var server = ServerWithLogs(("latest.log", new[]
        {
            "[22:00:00] [Server thread/INFO]: Alice joined the game",
            "[01:00:00] [Server thread/INFO]: Alice left the game",
        }));
        File.SetLastWriteTime(Path.Combine(server, "logs", "latest.log"), new DateTime(2026, 9, 18, 1, 0, 5));
        var store = Store();

        PlayerLogImporter.ImportOnce(server, store, serverRunning: false);

        Assert.Equal(Local(2026, 9, 17, 22, 0, 0), store.Get("Alice")!.LastJoin);
        Assert.Equal(Local(2026, 9, 18, 1, 0, 0), store.Get("Alice")!.LastLeave);
    }

    [Fact]
    public void WhileTheServerRunsLatestLogIsLeftToTheLiveConsole()
    {
        var server = ServerWithLogs(("latest.log", new[] { "[22:00:00] [Server thread/INFO]: Alice joined the game" }));
        var store = Store();

        PlayerLogImporter.ImportOnce(server, store, serverRunning: true);

        Assert.Null(store.Get("Alice"));
    }

    [Fact]
    public void ImportingTwiceChangesNothing()
    {
        _now = Local(2026, 9, 19, 12, 0, 0);
        var server = ServerWithLogs(("2026-09-10-1.log.gz", new[]
        {
            "[10:05:00] [Server thread/INFO]: Alice joined the game",
            "[11:05:00] [Server thread/INFO]: Alice left the game",
        }));
        var store = Store();

        PlayerLogImporter.ImportOnce(server, store, serverRunning: false);
        Assert.Equal(0, PlayerLogImporter.ImportOnce(server, store, serverRunning: false));

        Assert.Equal(2, store.Events("Alice").Count);
        Assert.Equal(1, store.Get("Alice")!.Sessions);
    }

    [Fact]
    public void ImportedLogsRespectTheRetention()
    {
        _now = Local(2026, 9, 19, 12, 0, 0);
        _settings = new PlayerHistorySettings { RetentionDays = 30 };
        var server = ServerWithLogs(
            ("2025-01-01-1.log.gz", new[] { "[10:00:00] [Server thread/INFO]: Old joined the game" }),
            ("2026-09-10-1.log.gz", new[] { "[10:00:00] [Server thread/INFO]: Alice joined the game" }));
        var store = Store();

        PlayerLogImporter.ImportOnce(server, store, serverRunning: false);

        Assert.Null(store.Get("Old"));
        Assert.NotNull(store.Get("Alice"));
    }

    [Fact]
    public void AnOldLogDoesNotCloseALiveSession()
    {
        // The server is running with Bob on it while the import reads a log that ended with Alice
        // still connected. Only Alice's session is closed.
        _now = Local(2026, 9, 19, 12, 0, 0);
        var store = Store();
        store.Record(Join("Bob"), _now.AddMinutes(-5));
        var server = ServerWithLogs(("2026-09-10-1.log.gz", new[] { "[10:00:00] [Server thread/INFO]: Alice joined the game" }));

        PlayerLogImporter.ImportOnce(server, store, serverRunning: true);

        Assert.Null(store.Get("Alice")!.OpenSince);
        Assert.NotNull(store.Get("Bob")!.OpenSince);
    }

    [Fact]
    public void ServersWithoutLogsAreMarkedImportedToo()
    {
        var store = Store();
        PlayerLogImporter.ImportOnce(Path.Combine(_root, "nothing-here"), store, serverRunning: false);
        Assert.True(store.Imported);
    }

    // --- The server's own statistics ---

    [Fact]
    public void TheServersStatisticsAreRead()
    {
        const string json = """
            {"stats":{"minecraft:custom":{"minecraft:play_time":72000,"minecraft:deaths":3,
              "minecraft:mob_kills":41,"minecraft:player_kills":1,"minecraft:walk_one_cm":150000,
              "minecraft:sprint_one_cm":50000,"minecraft:fall_one_cm":99999}},"DataVersion":4700}
            """;

        var stats = PlayerStatsReader.Parse(json)!;

        Assert.Equal(TimeSpan.FromHours(1), stats.PlayTime);
        Assert.Equal(3, stats.Deaths);
        Assert.Equal(41, stats.MobKills);
        Assert.Equal(1, stats.PlayerKills);
        Assert.Equal(200000, stats.DistanceCm);   // falling is not travelling
    }

    [Theory]
    [InlineData("stats")]            // up to 1.21
    [InlineData("players/stats")]    // from 26.1
    public void StatisticsAreFoundWhereverTheVersionKeepsThem(string folder)
    {
        var server = Path.Combine(_root, "stats-" + Guid.NewGuid().ToString("N"));
        const string uuid = "069a79f4-44e9-4726-a5be-fca90e38aaf5";
        var dir = Path.Combine(server, "world", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, uuid + ".json"), """{"stats":{"minecraft:custom":{"minecraft:deaths":7}}}""");

        Assert.Equal(7, PlayerStatsReader.Read(server, uuid)!.Deaths);
        Assert.Null(PlayerStatsReader.Read(server, "../../etc"));
        Assert.Null(PlayerStatsReader.Read(server, null));
    }

    [Fact]
    public void SettingsFromAnEditedFileStayInRange()
    {
        var clamped = new PlayerHistorySettings { MaxEventsPerPlayer = 0, RetentionDays = -5 }.Clamped();
        Assert.Equal(PlayerHistorySettings.MinEvents, clamped.MaxEventsPerPlayer);
        Assert.Equal(PlayerHistorySettings.MinDays, clamped.RetentionDays);
    }
}
