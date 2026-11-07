using System.Text.RegularExpressions;

namespace McServerLauncher.Tests;

/// <summary>
/// The notices file in the repository (<c>web/news.json</c>) and what keeps its bytes the bytes that
/// were signed.
/// </summary>
/// <remarks>
/// The app checks the signature over the bytes the server sends. This machine has
/// <c>core.autocrlf=true</c> and <c>.gitattributes</c> sets <c>* text=auto</c>, so without a rule of its
/// own a checkout would hand the file back with CRLF, the script would sign that, git would publish
/// LF, and every app would refuse the file. These tests are the net under that rule.
/// </remarks>
public class NewsFileTests
{
    private static string Root => LocalizationTests.RepoRoot();

    [Fact]
    public void GitIsToldNeverToTouchTheLineEndingsOfTheNoticesFile()
    {
        var attributes = File.ReadAllLines(Path.Combine(Root, ".gitattributes"));

        Assert.Contains(attributes, line =>
            Regex.IsMatch(line, @"^web/news\.json\s+-text\s*$"));
    }

    [Fact]
    public void TheNoticesFileNeverHoldsACarriageReturnOrAByteOrderMark()
    {
        // Passes while the file does not exist yet: it arrives with the step that activates the feed.
        var path = Path.Combine(Root, "web", "news.json");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);

        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "web/news.json must be UTF-8 without a byte-order mark: the signature is over the bytes.");
        Assert.True(bytes.Length <= 64 * 1024, "the app refuses a notices file over 64 KiB");
    }

    [Fact]
    public void TheSigningScriptRefusesWhatTheSignatureCouldNotSurvive()
    {
        // The script is the publisher's last guard. Each refusal here is a case that would otherwise
        // produce a signature the apps reject, or a notice the app drops without a word.
        var script = File.ReadAllText(Path.Combine(Root, "tools", "signing", "sign-news.ps1"));

        Assert.Contains("$bytes -contains 13", script);                 // CR
        Assert.Contains("$bytes[0] -eq 0xEF", script);                  // byte-order mark
        Assert.Contains("65536", script);                               // 64 KiB
        Assert.Contains("'until'", script);                             // urgent notices need an end
        Assert.Contains("'resolvedAt'", script);                        // resolved ones need a date
    }
}
