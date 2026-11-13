using System.Net;
using System.Net.Http;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A server that goes quiet after its headers, and one that answers with more than anything real
/// would.
/// </summary>
/// <remarks>
/// <para>
/// <c>HttpClient.Timeout</c> stops counting when the headers arrive if the request asked for
/// <c>ResponseHeadersRead</c>, which every download does. A connection that then stopped sending
/// waited for ever: the update box stayed on "Downloading", and the news check, which holds a lock
/// while it reads, made every later check queue behind it.
/// </para>
/// <para>
/// The deadline here is per read, not per file, so these tests use a few hundred milliseconds where
/// the app uses <see cref="AppHttp.ReadIdleTimeout"/>.
/// </para>
/// </remarks>
public class NetworkStallTests : IDisposable
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(300);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-stall-" + Guid.NewGuid().ToString("N"));

    public NetworkStallTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { /* best effort */ }
    }

    /// <summary>Sends <paramref name="first"/> bytes and then nothing, until the read is cancelled.</summary>
    private sealed class StallingStream(int first) : Stream
    {
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent < first)
            {
                var n = Math.Min(buffer.Length, first - _sent);
                _sent += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Hands out one byte every <paramref name="gap"/>, <paramref name="total"/> in all: slow, never silent for long.</summary>
    private sealed class TricklingStream(int total, TimeSpan gap) : Stream
    {
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent >= total) return 0;
            await Task.Delay(gap, ct);
            buffer.Span[0] = 1;
            _sent++;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Answer(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    [Fact]
    public async Task AReadThatGoesQuietIsGivenUpOn()
    {
        var started = DateTime.UtcNow;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            AppHttp.ReadCappedAsync(new StreamContent(new StallingStream(10)), 1_000, CancellationToken.None, Idle));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "it should not have waited");
    }

    [Fact]
    public async Task ASlowReadThatKeepsSendingIsNotCut()
    {
        // 15 bytes at 100 ms each is 1.5 s in all, five times the idle limit: slow is not stalled.
        var bytes = await AppHttp.ReadCappedAsync(
            new StreamContent(new TricklingStream(15, TimeSpan.FromMilliseconds(100))), 1_000, CancellationToken.None, Idle);

        Assert.Equal(15, bytes.Length);
    }

    [Fact]
    public async Task CancellingIsStillACancellationAndNotATimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppHttp.ReadCappedAsync(new StreamContent(new StallingStream(0)), 1_000, cts.Token, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ADownloadThatGoesQuietLeavesWhatWasThereAndNoPartFile()
    {
        var dest = Path.Combine(_folder, "installer.exe");
        File.WriteAllText(dest, "the working copy");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            AtomicDownload.ToFileAsync(new StreamContent(new StallingStream(500)), dest, idleTimeout: Idle));

        Assert.Equal("the working copy", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task ASlowDownloadThatKeepsSendingArrivesWhole()
    {
        var dest = Path.Combine(_folder, "slow.jar");

        await AtomicDownload.ToFileAsync(
            new StreamContent(new TricklingStream(15, TimeSpan.FromMilliseconds(100))), dest, idleTimeout: Idle);

        Assert.Equal(15, new FileInfo(dest).Length);
    }

    /// <summary>
    /// The Playit calls used to read their answers with no ceiling at all (and buffered them
    /// whole before the code could look): an answer of any size was accepted.
    /// </summary>
    [Fact]
    public async Task AnAnswerFarLargerThanAnyRealOneIsRefusedByThePlayitSetup()
    {
        var http = new HttpClient(new Answer(() =>
            new StreamContent(new MemoryStream(new byte[(int)AppHttp.MaxApiBytes + 1024]))));
        var service = new PlayitPartnerService("https://proxy.example", http);

        await Assert.ThrowsAsync<UserFacingException>(() => service.CreateAgentAsync("abc123"));
    }

    /// <summary>
    /// The three calls that read a whole answer into memory with no ceiling and no deadline. Fifteen
    /// of them were scattered through the services; nothing stopped the sixteenth.
    /// </summary>
    [Fact]
    public void NoCodeReadsAnAnswerWithoutACapAndADeadline()
    {
        var app = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher");
        var offenders = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: Path.GetRelativePath(app, f), Line: i + 1, Text: line.Trim()))
                .Where(l => !l.Text.StartsWith("//") && !l.Text.StartsWith("///")
                            && (l.Text.Contains(".GetStringAsync(") || l.Text.Contains(".ReadAsStringAsync(")
                                || l.Text.Contains(".ReadFromJsonAsync<"))))
            .Select(l => $"{l.File}:{l.Line}  {l.Text}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "read through AppHttp (GetStringCappedAsync, ReadCappedStringAsync, ReadJsonCappedAsync) instead:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public async Task TheJsonReaderHonoursTheSameCeiling()
    {
        var content = new StringContent("{\"a\":1}");

        Assert.Equal(1, (await AppHttp.ReadJsonCappedAsync<Dictionary<string, int>>(content, 100, CancellationToken.None))!["a"]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppHttp.ReadJsonCappedAsync<Dictionary<string, int>>(new StringContent("{\"a\":1}"), 3, CancellationToken.None));
    }
}
