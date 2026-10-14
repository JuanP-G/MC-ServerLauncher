using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// What to tell a person about a failure, instead of what the exception says.
/// </summary>
/// <remarks>
/// <para>
/// Every place that showed an error used to put <c>ex.Message</c> on screen. That text is written
/// for developers, by .NET or the OS: "The process cannot access the file '…\server.jar' because it
/// is being used by another process", in English or in the system's language, inside a sentence in
/// the app's. This is the one place that turns a failure into words the app chose.
/// </para>
/// <para>
/// A <see cref="UserFacingException"/> is shown as it is: its message was already written for the
/// user. The failures a person can do something about — a file in use, no permission, a full disk,
/// no connection — get a sentence each that says what to do. Anything else gets a plain "something
/// went wrong". In every case the exception itself, with its message and stack, goes to the day's
/// log, so nothing a bug report needs is lost by not showing it.
/// </para>
/// </remarks>
public static class ErrorMessages
{
    /// <summary>The sentence to show for <paramref name="error"/>; its details go to the log.</summary>
    public static string Describe(Exception error) =>
        Describe(error, details => ConsoleLogService.Shared.Log("error", details));

    /// <summary>
    /// <see cref="Describe(Exception)"/> with the log handed in, so tests can see what is logged
    /// without writing to the user's application data.
    /// </summary>
    internal static string Describe(Exception error, Action<string> log)
    {
        log(error.ToString());

        var cause = Unwrap(error);
        return cause is UserFacingException ? cause.Message : Format(cause, Localizer.Get(KeyFor(cause)));
    }

    /// <summary>The resource key for a failure, before any value is filled in.</summary>
    internal static string KeyFor(Exception error) => error switch
    {
        PlayitApiException { IsReadOnlyRefusal: true } => "Tun_Err_ReadOnly",
        PlayitApiException { IsAuthError: true } => "Tun_Err_Key",
        PlayitApiException => "Err_Playit",
        HttpRequestException { StatusCode: not null } => "Err_HttpStatusFmt",
        HttpRequestException => "Err_NoConnection",
        TimeoutException or TaskCanceledException { InnerException: TimeoutException } => "Err_Timeout",
        OperationCanceledException => "Err_Cancelled",
        UnauthorizedAccessException => "Err_NoPermission",
        DirectoryNotFoundException or FileNotFoundException => "Err_NotFound",
        IOException io when IsInUse(io) => "Err_FileInUse",
        IOException io when IsDiskFull(io) => "Err_DiskFull",
        InvalidDataException or JsonException => "Err_Damaged",
        _ => "Err_Unexpected",
    };

    private static string Format(Exception error, string text) => error switch
    {
        HttpRequestException { StatusCode: { } status } => string.Format(text, (int)status),
        _ => text,
    };

    /// <summary>The one failure inside an <see cref="AggregateException"/> that holds only one.</summary>
    private static Exception Unwrap(Exception error) =>
        error is AggregateException { InnerExceptions.Count: 1 } many
            ? Unwrap(many.InnerExceptions[0])
            : error;

    /// <summary>Another program holds the file: a running server, an antivirus, an open editor.</summary>
    /// <remarks>
    /// Windows reports it as a sharing or lock violation (32, 33). Linux and macOS only lock files
    /// by agreement, so the only "in use" they report is a busy device or a running executable.
    /// </remarks>
    private static bool IsInUse(IOException error) => OperatingSystem.IsWindows()
        ? (uint)error.HResult is 0x80070020 or 0x80070021
        : error.HResult is 16 or 26;   // EBUSY, ETXTBSY

    private static bool IsDiskFull(IOException error) => OperatingSystem.IsWindows()
        ? (uint)error.HResult is 0x80070027 or 0x80070070
        : error.HResult == 28;         // ENOSPC
}
