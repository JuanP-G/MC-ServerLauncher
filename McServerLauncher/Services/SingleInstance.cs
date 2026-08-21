using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Keeps the app to one running copy per user, and lets a second launch bring the first one back
/// to the front instead of opening a window of its own.
/// </summary>
/// <remarks>
/// <para>
/// Two copies of the launcher is not merely untidy: each one starts its own child server processes,
/// its own wake-on-demand listeners and its own idle timers, and neither can see what the other is
/// doing. The second window shows a server as stopped while the first has it running, so Start
/// looks available — and pressing it means two JVMs writing the same world folder, which is how
/// worlds get corrupted. This is a correctness guard, not a nicety.
/// </para>
/// <para>
/// The design is a lock file plus a named pipe. The lock file answers "is anyone else running?"
/// with an exclusive open that the OS releases even if the process is killed, so a crash can never
/// leave the app permanently unstartable — the failure mode a stale PID file would have. The pipe
/// carries the "come to the front" nudge. Both work the same on Windows, Linux and macOS: .NET
/// implements named pipes as Unix domain sockets where there are none.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Raised on a background thread when another launch asks for the window. </summary>
    public event Action? ActivationRequested;

    private readonly FileStream _lock;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(FileStream held) => _lock = held;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher");

    /// <summary>
    /// Which single-instance namespace this is. Null in production, where there is exactly one.
    /// </summary>
    /// <remarks>
    /// It feeds both the lock file's name and the pipe's, because the two have to agree: they are
    /// the two halves of one mechanism. Tests set it so they can drive the real lock and the real
    /// pipe without fighting the user's own running copy for them.
    /// </remarks>
    internal static string? Scope { get; set; }

    private static string LockPath =>
        Path.Combine(Dir, Scope is null ? "instance.lock" : $"instance-{Scope}.lock");

    /// <summary>
    /// The pipe name, which has to be unique per user but identical between two launches by the
    /// same user.
    /// </summary>
    /// <remarks>
    /// Per user, because on a shared machine (or a Windows box with fast user switching) two people
    /// each running their own copy are not a conflict, and one must not be able to poke the other's
    /// window. The name is hashed rather than the raw user name because it becomes a path —
    /// /tmp/CoreFxPipe_&lt;name&gt; on Unix — and user names may contain characters that are not
    /// welcome there.
    /// </remarks>
    private static string PipeName
    {
        get
        {
            var who = Environment.UserName + "@" + Environment.MachineName + "/" + Scope;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(who));
            return "mc-server-launcher-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Claims the right to be the running copy.
    /// </summary>
    /// <param name="alreadyRunning">
    /// True only when another copy demonstrably holds the lock. It is deliberately separate from a
    /// null return: "someone else is running" and "this machine would not let me find out" are
    /// opposite situations, and conflating them would either close a legitimate first launch or let
    /// a second one through.
    /// </param>
    /// <returns>
    /// The claim to hold open for the lifetime of the app, or null if it was not obtained — in which
    /// case <paramref name="alreadyRunning"/> says whether that means "yield" or "carry on anyway".
    /// </returns>
    public static SingleInstance? TryAcquire(out bool alreadyRunning)
    {
        alreadyRunning = false;
        try
        {
            Directory.CreateDirectory(Dir);

            // FileShare.None is the whole mechanism: the second process's open fails, and the OS
            // drops the lock when this process ends however it ends — including a hard kill, which
            // is why this is a lock and not a PID file.
            var held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                      FileShare.None, 1);
            var instance = new SingleInstance(held);
            instance.Listen();
            return instance;
        }
        catch (IOException)
        {
            alreadyRunning = true;      // someone else holds it: the normal second-launch path
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            alreadyRunning = true;
            return null;
        }
        catch (Exception ex)
        {
            // Anything else (an exotic filesystem, a locked-down profile) must not stop the app
            // from opening at all. Letting the launch through is the safe direction to fail: worst
            // case the user gets exactly the behaviour they had before this existed.
            Debug.WriteLine("single instance: " + ex);
            return null;
        }
    }

    /// <summary>
    /// Asks the copy that is already running to show itself. True when it acknowledged.
    /// </summary>
    public static bool SignalExistingInstance()
    {
        try
        {
            // Windows only lets the process that currently owns the foreground hand it over. This
            // process does: the user just launched it, which is exactly the case the rule exists to
            // allow. Without this the other copy can un-hide its window but cannot bring it in
            // front of whatever the user was looking at, so a second launch appears to do nothing.
            if (OperatingSystem.IsWindows())
                try { AllowSetForegroundWindow(AsfwAny); } catch { /* older or locked-down Windows */ }

            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);

            // Short: if the other process is wedged, the user is better served by this launch
            // failing visibly than by a window that never appears and never says why.
            pipe.Connect(3000);
            pipe.WriteByte(1);
            pipe.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Lets any process take the foreground from this one. See SignalExistingInstance.</summary>
    private const int AsfwAny = -1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>How long an accepted connection has to actually say something.</summary>
    private static readonly TimeSpan SignalReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Waits for other launches, one at a time, until the app closes.</summary>
    private void Listen() => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);

                // ReadByte() would be synchronous, unbounded and deaf to the token: anything that
                // connected and then never wrote — a second launch that dies between Connect and
                // Write is enough — would block this loop for good. From that moment on, launching
                // the app again quietly stops bringing the window back, with nothing to show why.
                //
                // A deadline on top of the token is what fixes it. The token alone only fires when
                // the app closes, so a silent peer would still hold the loop until then; a real
                // second launch writes its byte the instant it connects, so this costs it nothing.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                attempt.CancelAfter(SignalReadTimeout);

                var signal = new byte[1];
                if (await server.ReadAsync(signal, attempt.Token) > 0) ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Only a real shutdown ends the loop. Anything else is one abandoned connection
                // hitting its deadline, and dropping it is the entire point — taking the listener
                // down with it would be the bug this guards against.
                if (_cts.IsCancellationRequested) return;
            }
            catch (Exception ex)
            {
                // A broken connection must not kill the listener: it has to survive to answer the
                // next launch. Pause first, so a pipe that fails instantly can't spin the CPU.
                Debug.WriteLine("single instance listener: " + ex);
                try { await Task.Delay(500, _cts.Token); } catch { return; }
            }
        }
    });

    public void Dispose()
    {
        _cts.Cancel();
        try { _lock.Dispose(); } catch { /* going away anyway */ }
        _cts.Dispose();
    }
}
