using System.Text.RegularExpressions;

namespace McServerLauncher.Tests;

/// <summary>
/// A timer built away from the visuals says which thread it belongs to.
/// </summary>
/// <remarks>
/// Since Avalonia 12 a bare <c>new DispatcherTimer</c> takes the dispatcher of the thread that builds it.
/// On a server's output thread or a thread pool thread that dispatcher runs no loop, so the timer never
/// ticks, with nothing said: the same code gave 17 ticks a second on Avalonia 11 and none on 12. Several of
/// these are built late, the first time something needs them, which makes the thread easy to get wrong
/// later. A control or a behaviour is on the UI thread by being attached to the tree, so those are left out.
/// </remarks>
public class DispatcherTimerTests
{
    [Fact]
    public void EveryTimerOutsideTheVisualsNamesTheUiThread()
    {
        var root = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher");
        var offenders = new[] { "ViewModels", "Services", "Views" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, i)))
            .Where(x => Regex.IsMatch(x.line, @"new DispatcherTimer\b") && !x.line.Contains("Dispatcher.UIThread"))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.i + 1}")
            .ToList();

        Assert.Empty(offenders);
    }
}
