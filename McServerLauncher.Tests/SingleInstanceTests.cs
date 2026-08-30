using System.IO.Pipes;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// One running copy per user, and a second launch that brings the first one's window back.
/// </summary>
/// <remarks>
/// These drive the real lock file and the real named pipe — mocking them would test the mock, and
/// the whole mechanism <em>is</em> the operating system's behaviour. <see cref="SingleInstance.Scope"/>
/// keeps them in their own namespace so they never touch the copy of the app the developer has open,
/// and each test gets a fresh one so they cannot inherit each other's leftovers either.
/// </remarks>
public class SingleInstanceTests : IDisposable
{
    // A scope of its own per test, not one shared by the class. xUnit builds a fresh instance for
    // each test, and on Linux a named pipe is a socket file in /tmp: a listener from the previous
    // test still letting go of that path made the next one unable to bind, which failed on CI and
    // not on Windows.
    public SingleInstanceTests() =>
        SingleInstance.Scope = "tests-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose() => SingleInstance.Scope = null;

    [Fact]
    public void SecondClaimIsRefusedWhileTheFirstHoldsIt()
    {
        using var first = SingleInstance.TryAcquire(out var alreadyRunning);
        Assert.NotNull(first);
        Assert.False(alreadyRunning);

        var second = SingleInstance.TryAcquire(out var seesTheFirst);

        Assert.Null(second);

        // "Someone else is running" and "the mechanism failed" are handled in opposite ways, so
        // this flag carries the whole decision: yield, or carry on and open anyway.
        Assert.True(seesTheFirst);
    }

    [Fact]
    public void LockIsReleasedWhenTheHolderGoesAway()
    {
        var first = SingleInstance.TryAcquire(out _);
        Assert.NotNull(first);
        first!.Dispose();

        using var second = SingleInstance.TryAcquire(out var alreadyRunning);

        Assert.NotNull(second);
        Assert.False(alreadyRunning);
    }

    [Fact]
    public async Task SecondLaunchAsksTheFirstToShowItself()
    {
        using var running = SingleInstance.TryAcquire(out _);
        Assert.NotNull(running);

        var asked = new TaskCompletionSource();
        running!.ActivationRequested += () => asked.TrySetResult();

        // Wait for the listener before signalling. TryAcquire starts it asynchronously, so
        // signalling straight away raced it: the connection was refused and the test failed about
        // once in five, blaming the code rather than its own timing. Its neighbour below already
        // used this helper; this one did not.
        (await ConnectWhenListening()).Dispose();

        Assert.True(SingleInstance.SignalExistingInstance());

        var arrived = await Task.WhenAny(asked.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(asked.Task, arrived);
    }

    [Fact]
    public async Task AConnectionThatNeverSpeaksDoesNotKillActivation()
    {
        // The finding. A second launch that dies between Connect and Write used to leave the
        // listener blocked in a synchronous read for good: from then on, launching the app again
        // silently stopped bringing the window back, with nothing anywhere to say why.
        using var running = SingleInstance.TryAcquire(out _);
        Assert.NotNull(running);

        var asked = new TaskCompletionSource();
        running!.ActivationRequested += () => asked.TrySetResult();

        // Connect, say nothing, and — this is the part that matters — stay connected. Closing the
        // pipe would end the read by itself and prove nothing; the bug is a peer that holds the
        // connection open in silence.
        using var mute = await ConnectWhenListening();
        await Task.Delay(200);

        // The listener has to recover on its own and answer the next launch. Generous, because it
        // must outlast the abandoned connection's own deadline.
        var recovered = false;
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < limit && !recovered)
        {
            if (SingleInstance.SignalExistingInstance() &&
                await Task.WhenAny(asked.Task, Task.Delay(1000)) == asked.Task)
            {
                recovered = true;
            }
        }

        Assert.True(recovered, "la activación quedó colgada tras una conexión que no escribió nada");
    }

    /// <summary>
    /// Connects once the listener is actually up, rather than assuming it already is.
    /// </summary>
    /// <remarks>
    /// The listener is started on a background task, and on Unix the socket only exists from the
    /// first WaitForConnection onwards, so a connect issued immediately after acquiring can arrive
    /// before there is anything to connect to.
    /// </remarks>
    private static async Task<NamedPipeClientStream> ConnectWhenListening()
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", PipeNameForTests(), PipeDirection.Out);
            try
            {
                pipe.Connect(500);
                return pipe;
            }
            catch (TimeoutException)
            {
                pipe.Dispose();
                if (DateTime.UtcNow > limit) throw;
                await Task.Delay(100);
            }
        }
    }

    /// <summary>The same name the service builds, so the test connects where it is really listening.</summary>
    private static string PipeNameForTests()
    {
        var field = typeof(SingleInstance).GetProperty("PipeName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)field!.GetValue(null)!;
    }
}
