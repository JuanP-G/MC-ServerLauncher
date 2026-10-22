using System.IO;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Reads the lines a file gains, between one call and the next: the GC log the JVM keeps writing.
/// </summary>
/// <remarks>
/// <para>
/// The JVM owns the file, so it is opened letting it go on writing, rotating and deleting
/// (<c>FileShare.ReadWrite | Delete</c>). A line the JVM is half way through writing is left for
/// the next call. When the JVM rotates the log, the file the app was reading becomes
/// <c>gc.log.0</c> and a new <c>gc.log</c> starts; that shows as a new creation time or a shorter
/// file, and reading starts again at the top.
/// </para>
/// <para>
/// At most <see cref="MaxBytesPerRead"/> per call, so a long-forgotten tray window catching up
/// never holds anything for long. Blocking file I/O: call it off the UI thread.
/// </para>
/// </remarks>
public sealed class GcLogTail
{
    /// <summary>The most one call reads.</summary>
    public const int MaxBytesPerRead = 256 * 1024;

    private readonly string _path;
    private long _position;
    private DateTime _created;

    public GcLogTail(string path) => _path = path;

    /// <summary>The complete lines written since the last call. Empty when the file is not there (yet).</summary>
    public IReadOnlyList<string> ReadNewLines()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var created = File.GetCreationTimeUtc(_path);
            if (created != _created || stream.Length < _position)
            {
                _position = 0;
                _created = created;
            }

            var available = stream.Length - _position;
            if (available <= 0) return [];

            var buffer = new byte[Math.Min(available, MaxBytesPerRead)];
            stream.Seek(_position, SeekOrigin.Begin);
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return [];

            // Only up to the last line break; the rest is a line still being written. A chunk with no
            // break at all and no room left is not a log line, so it is dropped instead of stalling.
            var end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (end < 0)
            {
                if (read < MaxBytesPerRead) return [];
                end = read - 1;
            }

            _position += end + 1;
            return Encoding.UTF8.GetString(buffer, 0, end + 1)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .ToList();
        }
        catch (IOException)
        {
            return [];   // not there yet, or being rotated this instant: the next call finds it
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
