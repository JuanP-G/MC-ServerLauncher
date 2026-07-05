using Avalonia.Controls;
using Avalonia.Threading;

namespace McServerLauncher.Views;

/// <summary>
/// Buffered progress logger for dialog TextBoxes (EFI-6): the Forge installer prints thousands of
/// lines, and appending each one directly (Text += line) froze the UI. Lines are buffered and
/// flushed a few times per second, keeping only the most recent <see cref="MaxLines"/>. Extracted
/// from CreateServerDialog and InstallLoaderDialog, which kept two identical copies of this logic.
/// Call <see cref="Stop"/> from the dialog's OnClosed.
/// </summary>
public sealed class LogBatcher
{
    private const int MaxLines = 400;

    private readonly TextBox _target;
    private readonly List<string> _lines = new();
    private readonly DispatcherTimer _timer;
    private bool _dirty;

    public LogBatcher(TextBox target)
    {
        _target = target;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    /// <summary>Queues a line (called via Progress&lt;string&gt;, i.e. on the UI thread).</summary>
    public void Append(string line)
    {
        _lines.Add(line);
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        _dirty = true;
    }

    private void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        _target.Text = string.Join(Environment.NewLine, _lines) + Environment.NewLine;
        _target.CaretIndex = _target.Text.Length;
    }

    public void Stop() => _timer.Stop();
}
