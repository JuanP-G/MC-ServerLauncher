using Avalonia;
using McServerLauncher.Services;

namespace McServerLauncher;

/// <summary>Cross-platform entry point (Avalonia desktop lifetime).</summary>
public static class Program
{
    /// <summary>
    /// This copy's claim to being the only one running, held for as long as the app lives.
    /// Null when the guard could not be used at all (see <see cref="SingleInstance.TryAcquire"/>).
    /// </summary>
    public static SingleInstance? Instance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Before Avalonia, on purpose: a second launch must not build a window, a tray icon or a
        // set of ViewModels — each of which would start timers and wake listeners of its own —
        // only to tear them all down again.
        Instance = SingleInstance.TryAcquire(out var alreadyRunning);
        if (alreadyRunning)
        {
            // The normal case: hand over to the copy that is already up and stop here.
            if (SingleInstance.SignalExistingInstance()) return;

            // Nobody answered. The likeliest reason is that the other copy is on its way out and
            // has not let go yet — an update relaunches the app the moment the installer finishes,
            // and refusing to start then would leave the user with no app at all after updating.
            //
            // Retrying the claim is safe in a way that "start anyway" would not be: the lock is
            // exclusive, so getting it proves the other process is gone. If it is still held, this
            // gives up rather than risk two copies running the same servers.
            for (var attempt = 0; attempt < 4 && Instance is null; attempt++)
            {
                Thread.Sleep(500);
                Instance = SingleInstance.TryAcquire(out _);
            }

            if (Instance is null) return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance?.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
