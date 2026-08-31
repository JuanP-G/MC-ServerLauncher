using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit.Sdk;

namespace McServerLauncher.Tests;

/// <summary>
/// One headless Avalonia application, shared by every test that needs real controls.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia can only be set up once per process, and its controls must be touched from the thread
/// that set it up. Both facts are enforced here rather than left to each test: getting either wrong
/// produces failures that look like the code under test rather than the harness.
/// </para>
/// <para>
/// This exists because a bug shipped twice that only real controls could catch — the type picker
/// reporting the previous selection inside its own change event. A plain unit test could not see it,
/// and the manual harness that did does not run in CI.
/// </para>
/// </remarks>
public sealed class AvaloniaFixture : IDisposable
{
    private readonly Thread _ui;
    private readonly CancellationTokenSource _stop = new();

    public AvaloniaFixture()
    {
        var ready = new TaskCompletionSource();

        // Its own thread with a running dispatcher: controls raise property changes through it, and
        // without one nothing that posts to the UI thread would ever run.
        _ui = new Thread(() =>
        {
            AppBuilder.Configure<McServerLauncher.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                .SetupWithoutStarting();

            ready.SetResult();
            Dispatcher.UIThread.MainLoop(_stop.Token);
        })
        { IsBackground = true, Name = "avalonia-tests" };

        // Windows only: on Linux this throws PlatformNotSupportedException ("COM Interop is not
        // supported"), which took the whole Linux CI leg down with it. Avalonia does not need an
        // STA thread there; it is a Windows COM requirement.
        if (OperatingSystem.IsWindows()) _ui.SetApartmentState(ApartmentState.STA);
        _ui.Start();

        if (!ready.Task.Wait(TimeSpan.FromSeconds(30)))
            throw new XunitException("Avalonia headless no arrancó en 30 s");
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and rethrows what it throws there.</summary>
    /// <remarks>
    /// The rethrow matters: an assertion failing inside the dispatcher would otherwise be swallowed
    /// and the test would pass while the thing it checks is broken.
    /// </remarks>
    public void Run(Action action)
    {
        Exception? failure = null;

        Dispatcher.UIThread.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        if (failure is not null)
            throw new XunitException(failure.ToString());
    }

    /// <summary>
    /// Drains the work a control queued instead of doing straight away.
    /// </summary>
    /// <remarks>
    /// Setting a property raises its change notification synchronously, but handlers that post back
    /// to the dispatcher — which is most of what a dialog does — only run when it is pumped. Without
    /// this a test sees the state from before its own change and reads as a product bug.
    /// </remarks>
    public static void Pump() => Dispatcher.UIThread.RunJobs();

    public void Dispose() => _stop.Cancel();
}

/// <summary>Groups the UI tests so they share one application and never run in parallel.</summary>
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<AvaloniaFixture>;
