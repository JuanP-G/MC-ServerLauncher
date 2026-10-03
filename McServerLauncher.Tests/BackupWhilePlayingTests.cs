using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Backing up the world of a server that is running.
/// </summary>
/// <remarks>
/// <para>
/// Zipping a world the JVM is writing to gives a torn copy — some region files from before a save
/// and some from after — which is exactly the backup that looks fine until the day it is needed.
/// So the server is asked to let go of the world first: <c>save-off</c>, <c>save-all flush</c>,
/// wait for it to say it is done, copy, <c>save-on</c>.
/// </para>
/// <para>
/// The last step is the one worth testing hardest. A backup that fails is an annoyance; a server
/// left with saving switched off loses everything played until somebody restarts it.
/// </para>
/// </remarks>
public class BackupWhilePlayingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-hot-" + Guid.NewGuid().ToString("N"));

    public BackupWhilePlayingTests() => Directory.CreateDirectory(_dir);

    private ServerConfig ServerWithAWorld()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "world"));
        File.WriteAllText(Path.Combine(_dir, "world", "level.dat"), "not really nbt, but it is bytes");
        return new ServerConfig { Name = "survival", FolderPath = _dir, BackupRetention = 5, ManualBackupRetention = 5 };
    }

    /// <summary>A console that records what was sent and answers the flush when asked to.</summary>
    private sealed class FakeConsole
    {
        public List<string> Sent { get; } = new();
        public bool Answer { get; init; } = true;

        public void Send(string command) => Sent.Add(command);

        public Task<bool> WaitForSave(TimeSpan timeout) => Task.FromResult(Answer);
    }

    // --- The sequence ---

    [Fact]
    public async Task TheWorldIsReleasedAndPickedUpAgainAroundTheCopy()
    {
        var console = new FakeConsole();

        var zip = await LiveWorldBackup.RunAsync(new WorldBackupService(), ServerWithAWorld(), "auto",
            console.Send, console.WaitForSave);

        Assert.Equal(new[] { "save-off", "save-all flush", "save-on" }, console.Sent);
        Assert.NotNull(zip);
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task SavingIsSwitchedBackOnEvenWhenTheCopyFails()
    {
        var console = new FakeConsole();

        await Assert.ThrowsAsync<IOException>(() => LiveWorldBackup.RunAsync(
            new ThrowingBackupService(), ServerWithAWorld(), "auto", console.Send, console.WaitForSave));

        Assert.Equal(new[] { "save-off", "save-all flush", "save-on" }, console.Sent);
    }

    private sealed class ThrowingBackupService : WorldBackupService
    {
        public override Task<string?> CreateBackupAsync(ServerConfig config, string trigger,
            IProgress<string>? log = null, CancellationToken ct = default, string? protectFromPruning = null) =>
            throw new IOException("el disco dijo que no");
    }

    [Fact]
    public async Task TheCopyStillHappensWhenTheServerNeverConfirms()
    {
        // A server too busy to answer within the timeout is not a reason to skip the backup: the
        // flush has almost certainly finished, and no backup is worse than one a moment old.
        var console = new FakeConsole { Answer = false };

        var zip = await LiveWorldBackup.RunAsync(new WorldBackupService(), ServerWithAWorld(), "auto",
            console.Send, console.WaitForSave);

        Assert.NotNull(zip);
        Assert.Equal("save-on", console.Sent[^1]);
    }

    [Fact]
    public async Task TheListenerIsStartedBeforeTheCommandItWaitsFor()
    {
        // A small world can answer between the two, and a listener started afterwards would miss it
        // and then wait out the whole timeout for a save that already happened.
        var order = new List<string>();

        await LiveWorldBackup.RunAsync(new WorldBackupService(), ServerWithAWorld(), "auto",
            command => order.Add("sent " + command),
            _ => { order.Add("listening"); return Task.FromResult(true); });

        Assert.Equal(
            new[] { "sent save-off", "listening", "sent save-all flush", "sent save-on" },
            order);
    }

    // --- Recognising the server's answer ---

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: Saved the game")]
    [InlineData("[12:00:00] [Server thread/INFO]: Saved the world")]
    [InlineData("[12:00:00 INFO]: Saved the game ")]
    public void TheServerSayingItSavedIsRecognised(string line) =>
        Assert.True(SaveConfirmation.IsSaveFinished(line));

    [Theory]
    // A player can type this in chat, and chat goes through the console too. Matching it would let
    // anyone on the server cut a backup short.
    [InlineData("[12:00:00] [Server thread/INFO]: <Bob> Saved the game")]
    [InlineData("[12:00:00] [Server thread/INFO]: [Server] Saved the game")]
    [InlineData("[12:00:00] [Server thread/INFO]: Saving the game (this may take a moment!)")]
    [InlineData("Saved the game")]
    [InlineData("")]
    public void AnythingElseIsNot(string line) =>
        Assert.False(SaveConfirmation.IsSaveFinished(line));

    // --- The clock ---

    private static readonly DateTime Noon = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NothingIsDueBeforeTheIntervalRunsOut() =>
        Assert.Equal(BackupDue.NotYet, BackupSchedule.Due(Noon.AddMinutes(59), Noon, 60, playedSince: true));

    [Fact]
    public void ABackupIsDueOnceItDoes() =>
        Assert.Equal(BackupDue.Now, BackupSchedule.Due(Noon.AddMinutes(60), Noon, 60, playedSince: true));

    [Fact]
    public void ItIsSkippedWhenNobodyHasPlayed() =>
        Assert.Equal(BackupDue.Skip, BackupSchedule.Due(Noon.AddHours(5), Noon, 60, playedSince: false));

    [Fact]
    public void NothingIsDueWhenItIsSwitchedOff() =>
        Assert.Equal(BackupDue.NotYet,
            BackupSchedule.Due(Noon.AddHours(5), Noon, 60, playedSince: true, enabled: false));

    [Theory]
    [InlineData(0, 5)]      // a hand-edited zero must not mean "on every tick"
    [InlineData(-30, 5)]
    [InlineData(3, 5)]
    [InlineData(60, 60)]
    [InlineData(99999, 1440)]
    public void AnOddIntervalIsBroughtBackIntoRange(int stored, int expected) =>
        Assert.Equal(expected, BackupSchedule.Clamp(stored));

    [Fact]
    public void AZeroIntervalStillWaitsFiveMinutes()
    {
        // Because it is clamped where it is read, not only where it is typed: settings.json is a
        // file people can edit, and the dialog is not the only way a value gets in.
        Assert.Equal(BackupDue.NotYet, BackupSchedule.Due(Noon.AddMinutes(4), Noon, 0, playedSince: true));
        Assert.Equal(BackupDue.Now, BackupSchedule.Due(Noon.AddMinutes(5), Noon, 0, playedSince: true));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
