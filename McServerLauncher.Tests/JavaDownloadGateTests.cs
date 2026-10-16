using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Two servers that need the same Java do not download it at the same time.
/// </summary>
/// <remarks>
/// Every server asks its own <c>JavaService</c>, and each used the same archive path and the same
/// <c>.partial</c> folder for the one Java: starting two servers together made each delete what the
/// other was writing. Here the downloads are stand-ins that only record who is inside at once.
/// </remarks>
public class JavaDownloadGateTests
{
    private static async Task<int> Download(int major, Counter counter, TaskCompletionSource? hold = null)
    {
        return await JavaService.OneAtATimeAsync(major, async () =>
        {
            var now = counter.Enter(major);
            if (hold is not null) await hold.Task;
            else await Task.Delay(30);
            counter.Leave(major);
            return now;
        }, CancellationToken.None);
    }

    private sealed class Counter
    {
        private readonly Dictionary<int, int> _inside = new();
        public int MostAtOnce { get; private set; }
        public int Enter(int major)
        {
            lock (_inside)
            {
                _inside[major] = _inside.GetValueOrDefault(major) + 1;
                MostAtOnce = Math.Max(MostAtOnce, _inside[major]);
                return _inside[major];
            }
        }
        public void Leave(int major) { lock (_inside) _inside[major]--; }
    }

    [Fact]
    public async Task TheSameJavaIsDownloadedOneAtATime()
    {
        var counter = new Counter();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Download(8001, counter)));

        Assert.Equal(1, counter.MostAtOnce);
    }

    [Fact]
    public async Task DifferentJavasDoNotWaitForEachOther()
    {
        var counter = new Counter();
        var hold = new TaskCompletionSource();

        var first = Download(8002, counter, hold);
        var second = Download(8003, counter, hold);
        await Task.Delay(100);

        // Both are inside at once: if the second waited for the first it would still be at the gate.
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        hold.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task AWaitingDownloadCanBeCancelled()
    {
        var counter = new Counter();
        var hold = new TaskCompletionSource();
        var running = Download(8004, counter, hold);
        using var cts = new CancellationTokenSource();

        var waiting = JavaService.OneAtATimeAsync(8004, () => Task.FromResult(0), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        hold.SetResult();
        await running;
    }

    [Fact]
    public async Task AFailedDownloadLetsTheNextOneIn()
    {
        var none = CancellationToken.None;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            JavaService.OneAtATimeAsync<int>(8005, () => throw new InvalidOperationException(), none));

        Assert.Equal(7, await JavaService.OneAtATimeAsync(8005, () => Task.FromResult(7), none));
    }
}
