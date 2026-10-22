using System.IO;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Following a log the JVM is still writing: a line cut in half, a file that rotates under the
/// reader, one that is not there yet.
/// </summary>
public sealed class GcLogTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gclog-" + Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(_dir, "gc.log");

    public GcLogTailTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    /// <summary>Appends the way the JVM does: while someone else may have the file open.</summary>
    private void Append(string text)
    {
        using var f = new FileStream(Path_, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        f.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void A_file_that_is_not_there_yet_has_no_lines()
    {
        Assert.Empty(new GcLogTail(Path_).ReadNewLines());
    }

    [Fact]
    public void Each_call_gives_only_what_was_written_since_the_last()
    {
        var tail = new GcLogTail(Path_);
        Append("one\ntwo\n");
        Assert.Equal(["one", "two"], tail.ReadNewLines());

        Assert.Empty(tail.ReadNewLines());

        Append("three\n");
        Assert.Equal(["three"], tail.ReadNewLines());
    }

    [Fact]
    public void A_line_still_being_written_waits_for_its_end()
    {
        var tail = new GcLogTail(Path_);
        Append("complete\npart");
        Assert.Equal(["complete"], tail.ReadNewLines());

        Append("ial\n");
        Assert.Equal(["partial"], tail.ReadNewLines());
    }

    [Fact]
    public void Windows_line_endings_leave_no_trace()
    {
        var tail = new GcLogTail(Path_);
        Append("a\r\nb\r\n");
        Assert.Equal(["a", "b"], tail.ReadNewLines());
    }

    [Fact]
    public void A_file_that_is_replaced_by_a_new_one_is_read_from_its_top()
    {
        var tail = new GcLogTail(Path_);
        Append("old line one\nold line two\n");
        Assert.Equal(2, tail.ReadNewLines().Count);

        // What the JVM does when the log is full: the file moves aside and a new one starts.
        File.Move(Path_, Path_ + ".0");
        Thread.Sleep(30); // creation time is what tells the new file apart
        Append("new\n");

        Assert.Equal(["new"], tail.ReadNewLines());
    }

    [Fact]
    public void A_file_that_shrinks_is_read_from_its_top()
    {
        var tail = new GcLogTail(Path_);
        Append("a long first line\n");
        tail.ReadNewLines();

        File.WriteAllText(Path_, "x\n");
        Assert.Equal(["x"], tail.ReadNewLines());
    }

    [Fact]
    public void No_call_reads_more_than_the_limit()
    {
        var tail = new GcLogTail(Path_);
        var line = new string('x', 99) + "\n";
        var count = GcLogTail.MaxBytesPerRead / line.Length * 2;
        Append(string.Concat(Enumerable.Repeat(line, count)));

        var first = tail.ReadNewLines();
        Assert.InRange(first.Count, 1, count - 1);

        var all = first.Count;
        for (var i = 0; i < 10 && all < count; i++) all += tail.ReadNewLines().Count;
        Assert.Equal(count, all);
    }

    [Fact]
    public void A_file_that_is_being_written_can_be_read_and_written_meanwhile()
    {
        using var writer = new FileStream(Path_, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("before\n"u8);
        writer.Flush();

        var tail = new GcLogTail(Path_);
        Assert.Equal(["before"], tail.ReadNewLines());

        writer.Write("after\n"u8);
        writer.Flush();
        Assert.Equal(["after"], tail.ReadNewLines());
    }
}
