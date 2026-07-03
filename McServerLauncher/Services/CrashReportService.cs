using System;
using System.IO;
using System.Linq;

namespace McServerLauncher.Services;

/// <summary>
/// Looks for a Minecraft server crash report relevant to the server's most recent run, to show a
/// short human-readable reason alongside "the server exited unexpectedly" instead of just an exit
/// code. Vanilla/Fabric/Forge/Paper all write crash reports to "&lt;folder&gt;/crash-reports/" with
/// a stable, decade-old format: a "---- Minecraft Crash Report ----" header followed by a
/// "Description: ..." line summarizing what went wrong.
/// </summary>
public class CrashReportService
{
    /// <summary>
    /// Returns the short reason from the newest crash report under "&lt;folder&gt;/crash-reports/",
    /// if one was written at or after <paramref name="sinceUtc"/> (so a stale report from an earlier,
    /// unrelated crash isn't picked up). Null if there's no crash-reports folder or nothing recent.
    /// </summary>
    public string? FindRecentCrashReason(string folder, DateTime sinceUtc)
    {
        try
        {
            var dir = Path.Combine(folder, "crash-reports");
            if (!Directory.Exists(dir)) return null;

            // A couple of seconds of slack: the file's last-write time can be a hair earlier than
            // our recorded start time depending on clock/filesystem resolution.
            var cutoff = sinceUtc - TimeSpan.FromSeconds(2);

            var newest = Directory.EnumerateFiles(dir, "*.txt")
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTimeUtc >= cutoff)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return null;

            foreach (var line in File.ReadLines(newest.FullName))
            {
                if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                {
                    var reason = line["Description:".Length..].Trim();
                    return string.IsNullOrEmpty(reason) ? newest.Name : reason;
                }
            }
            // The file exists but doesn't match the expected format (a future Minecraft version
            // could change it): point to the file itself rather than showing nothing.
            return newest.Name;
        }
        catch
        {
            return null;
        }
    }
}
