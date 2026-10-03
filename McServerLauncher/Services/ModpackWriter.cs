using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Writes the modpack zip: the jars, and the text that explains them.
/// </summary>
/// <remarks>
/// <para>
/// This used to be a call to <see cref="ZipFile.CreateFromDirectory(string,string)"/> over a folder
/// assembled in <c>%TEMP%</c>, which meant every jar was copied to disk a second time before being
/// read back — a 2 GB pack needed 2 GB of scratch space it had no reason to need — and the export
/// could only run with a window open, so it had no tests at all.
/// </para>
/// <para>
/// Entry by entry instead: the jars are read from where they already are, and each text file is
/// written on its own terms. That last part is not a detail. A shell script with Windows line
/// endings fails with <c>$'\r': command not found</c>, which to the player reads as a broken pack.
/// </para>
/// </remarks>
internal static class ModpackWriter
{
    /// <summary>Text written into the pack, and how the thing that reads it needs it written.</summary>
    /// <param name="Name">Path inside the zip, always forward slashes.</param>
    /// <param name="Text">The contents, with <c>\n</c> line endings whatever the platform.</param>
    /// <param name="Newline">What those line endings become on the way in.</param>
    /// <param name="Executable">Ask for the Unix execute bit. See <see cref="MarkExecutable"/>.</param>
    internal sealed record TextFile(
        string Name, string Text, string Newline = "\n", bool Executable = false);

    /// <summary>UTF-8 with no byte-order mark, which is the only encoding every target here reads.</summary>
    /// <remarks>
    /// No BOM anywhere, and one place that decides so no caller can forget: a BOM on the first line
    /// of a <c>.bat</c> is printed to the console before <c>@echo off</c> can take effect, and a
    /// BOM ahead of a shell script's <c>#!</c> stops it being recognised as a script at all.
    /// </remarks>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Builds the zip at <paramref name="destination"/>, replacing whatever was there.</summary>
    internal static void Write(
        string destination,
        string contentFolder,
        IReadOnlyList<string> jarPaths,
        IReadOnlyList<TextFile> textFiles)
    {
        var folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        // Create, not OpenOrCreate: exporting again after deleting a mod must not leave the deleted
        // one sitting in the pack because the old archive was opened and added to.
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var jar in jarPaths)
        {
            var entry = zip.CreateEntry($"{contentFolder}/{Path.GetFileName(jar)}", CompressionLevel.Optimal);
            using var source = File.OpenRead(jar);
            using var target = entry.Open();
            source.CopyTo(target);
        }

        foreach (var file in textFiles)
        {
            var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
            if (file.Executable) MarkExecutable(entry);

            using var target = entry.Open();
            var bytes = Utf8NoBom.GetBytes(file.Text.Replace("\r\n", "\n").Replace("\n", file.Newline));
            target.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>Asks for <c>rwxr-xr-x</c> on a zip entry.</summary>
    /// <remarks>
    /// Asks, and no more than that. The bits go in the high half of <c>ExternalAttributes</c>, which
    /// most extractors only honour when the archive's "made by" byte says Unix — and .NET sets that
    /// from the operating system doing the writing, with no way to override it. So a pack built on
    /// Windows can perfectly well arrive without the bit. Nothing is allowed to depend on it: the
    /// invocation printed in the instructions is <c>bash install-mods-unix.sh</c>, which needs no
    /// execute bit and sidesteps macOS quarantine into the bargain.
    /// </remarks>
    private static void MarkExecutable(ZipArchiveEntry entry)
    {
        const int regularFile = 0x8000;   // S_IFREG
        const int rwxrxrx = 0x1ED;        // 0755
        entry.ExternalAttributes |= (regularFile | rwxrxrx) << 16;
    }
}
