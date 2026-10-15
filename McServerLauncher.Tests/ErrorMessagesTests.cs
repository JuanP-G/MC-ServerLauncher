using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A failure reaches the screen as a sentence the app wrote, never as the exception's own text.
/// </summary>
/// <remarks>
/// About fifty places showed <c>ex.Message</c>: a locked file read "The process cannot access the
/// file … because it is being used by another process" in the middle of a Spanish sentence, and a
/// failed download "Response status code does not indicate success: 404 (Not Found)."
/// </remarks>
public class ErrorMessagesTests
{
    private readonly List<string> _logged = new();

    private string Describe(Exception error) => ErrorMessages.Describe(error, _logged.Add);

    [Fact]
    public void AMessageWrittenForTheUserIsShownAsItIs()
    {
        Assert.Equal("Paper no tiene build para 1.21.9",
            Describe(new UserFacingException("Paper no tiene build para 1.21.9")));
    }

    [Fact]
    public void AFileHeldByAnotherProgramSaysSoInTheAppsWords()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcl-inuse-" + Guid.NewGuid().ToString("N"));
        try
        {
            IOException error;
            if (OperatingSystem.IsWindows())
            {
                using var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                error = Assert.ThrowsAny<IOException>(() => File.OpenRead(path));
            }
            else
            {
                // Linux and macOS lock by agreement only; what they do report is a busy file.
                error = new IOException("Text file busy", 26);
            }

            var text = Describe(error);
            Assert.Equal(Localizer.Get("Err_FileInUse"), text);
            Assert.DoesNotContain(path, text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "Err_NoPermission")]
    [InlineData(typeof(DirectoryNotFoundException), "Err_NotFound")]
    [InlineData(typeof(FileNotFoundException), "Err_NotFound")]
    [InlineData(typeof(InvalidDataException), "Err_Damaged")]
    [InlineData(typeof(JsonException), "Err_Damaged")]
    [InlineData(typeof(TimeoutException), "Err_Timeout")]
    [InlineData(typeof(OperationCanceledException), "Err_Cancelled")]
    [InlineData(typeof(NullReferenceException), "Err_Unexpected")]
    [InlineData(typeof(InvalidOperationException), "Err_Unexpected")]
    public void EachKindOfFailureHasItsOwnSentence(Type kind, string key)
    {
        var error = (Exception)Activator.CreateInstance(kind, "internal text nobody should read")!;

        var text = Describe(error);

        Assert.Equal(Localizer.Get(key), text);
        Assert.DoesNotContain("internal text", text);
    }

    [Fact]
    public void AFullDiskSaysSo()
    {
        var error = OperatingSystem.IsWindows()
            ? new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))
            : new IOException("No space left on device", 28);

        Assert.Equal(Localizer.Get("Err_DiskFull"), Describe(error));
    }

    [Fact]
    public void AnHttpErrorSaysItsCodeAndNothingElse()
    {
        var error = new HttpRequestException("Response status code does not indicate success: 404 (Not Found).",
            
            null, HttpStatusCode.NotFound);

        Assert.Equal(string.Format(Localizer.Get("Err_HttpStatusFmt"), 404), Describe(error));
    }

    [Fact]
    public void NoConnectionAndATimedOutRequestAreToldApart()
    {
        Assert.Equal(Localizer.Get("Err_NoConnection"),
            Describe(new HttpRequestException("No such host is known.")));
        Assert.Equal(Localizer.Get("Err_Timeout"),
            Describe(new TaskCanceledException("The request was canceled", new TimeoutException())));
    }

    [Fact]
    public void ATaskThatFailedForOneReasonIsDescribedByThatReason()
    {
        var error = new AggregateException(new UserFacingException("La copia no se pudo hacer."));

        Assert.Equal("La copia no se pudo hacer.", Describe(error));
    }

    [Fact]
    public void WhatIsNotShownIsWrittenToTheLog()
    {
        Describe(new InvalidOperationException("the detail a bug report needs"));

        Assert.Contains(_logged, line => line.Contains("the detail a bug report needs"));
    }

    /// <summary>
    /// The rule this class exists for, held by a test: no view, view model or service puts an
    /// exception's own message in front of the user. Logging it is fine.
    /// </summary>
    [Fact]
    public void NothingShowsAnExceptionsOwnMessage()
    {
        var source = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher");
        var message = new Regex(@"\b(ex|e|jex|error|exception|inner)\.Message\b");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar) ||
                relative.StartsWith("bin" + Path.DirectorySeparatorChar))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//") || !message.IsMatch(line)) continue;
                if (line.Contains("ConsoleLogService.Shared.Log(")) continue;   // the log is for details
                offenders.Add($"{relative}:{i + 1}: {line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "An exception's own message reaches the user; use ErrorMessages.Describe(ex) instead:\n  " +
            string.Join("\n  ", offenders));
    }
}
