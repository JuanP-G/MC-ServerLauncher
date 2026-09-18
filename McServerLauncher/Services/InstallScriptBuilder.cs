using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// The scripts that put a modpack's jars where the player's Minecraft will find them.
/// </summary>
/// <remarks>
/// <para>
/// The pack used to be a zip and a page of instructions: extract it, find your mods folder, move
/// whatever is already in there somewhere safe, copy these in. That is four chances to get it wrong
/// for somebody who only wanted to join a server, and the third one is where mods get deleted.
/// </para>
/// <para>
/// The shape of the script lives in an embedded template and every visible line comes from the
/// .resx files. Sixty lines of shell inside a <c>&lt;value&gt;</c> would be escaped XML — no
/// highlighting, unreviewable in a diff, and, worst of all, subject to the translation-parity
/// checks on logic that has to be identical in all five languages. This way each sentence gets
/// those checks and the logic gets none of them.
/// </para>
/// <para>
/// A token is always replaced by a whole finished message. Nothing the templates write is ever used
/// as a format string, so no translation can smuggle in a <c>%s</c>, and every value a script
/// prints is a shell variable the script itself set.
/// </para>
/// </remarks>
internal static class InstallScriptBuilder
{
    /// <summary>The published names. Not translated, on purpose.</summary>
    /// <remarks>
    /// Breaking with <c>Export_InstructionsFile</c>, which is translated, and for a reason that
    /// only applies to these two: a non-ASCII name inside a zip depends on the archive's encoding
    /// and on the extractor honouring it, and a script that arrives as <c>instalar-mods-ma?os.sh</c>
    /// is worse than one with an English name. The instructions file names them both.
    /// </remarks>
    internal const string WindowsName = "install-mods-windows.bat";

    /// <summary>One file for Linux and macOS; it branches on <c>uname -s</c>.</summary>
    internal const string UnixName = "install-mods-unix.sh";

    /// <summary>What the scripts look for in <c>versions/</c> to decide the loader is installed.</summary>
    private static string LoaderMark(ServerType type) => type switch
    {
        ServerType.Fabric => "fabric",
        ServerType.NeoForge => "neoforge",
        ServerType.Forge => "forge",
        _ => string.Empty
    };

    /// <summary>Where to send somebody whose loader is missing.</summary>
    private static string LoaderSite(ServerType type) => type switch
    {
        ServerType.Fabric => "https://fabricmc.net",
        ServerType.NeoForge => "https://neoforged.net",
        ServerType.Forge => "https://files.minecraftforge.net",
        _ => string.Empty
    };

    /// <summary>
    /// Every message key the scripts use, written out as literals.
    /// </summary>
    /// <remarks>
    /// The test that checks each key the code asks for actually exists only reads string literals,
    /// so a key assembled from a token name at run time would be invisible to it — and a missing
    /// one renders as its own name, inside a script, on somebody else's machine.
    /// </remarks>
    internal static readonly IReadOnlyList<string> MessageKeys = new[]
    {
        "Script_CloseGame", "Script_NoSource", "Script_AskFolder", "Script_DestinationIs",
        "Script_AskContinue", "Script_Cancelled", "Script_CannotCreate", "Script_MoveFailed",
        "Script_MovedAside", "Script_CopyFailed", "Script_Done", "Script_HowToUndo",
        "Script_LauncherOfficial", "Script_ArrowsHint", "Script_OtherPath", "Script_PickOne",
        "Script_TypePath", "Script_AskInstall", "Script_CarryingOn", "Script_Downloading",
        "Script_DownloadFailed", "Script_NoDownloader", "Script_NoJava", "Script_NoHashTool",
        "Script_HashMismatch", "Script_Installing", "Script_LoaderInstalled", "Script_InstallFailed"
    };

    /// <summary>The two scripts, ready to go into the pack.</summary>
    /// <param name="serverName">Shown in the header so a player with two packs can tell them apart.</param>
    /// <param name="type">Decides what the loader check looks for, and where it points if absent.</param>
    /// <param name="gameVersion">Shown in the header.</param>
    /// <param name="builtAtLocal">
    /// When the pack was made, which names the backup folder. Worked out here rather than in the
    /// script because <c>%DATE%</c> in a .bat comes out in the machine's local format, and in much
    /// of Europe that produces a folder name with slashes in it.
    /// </param>
    /// <param name="loader">
    /// The installer to offer, or null to only warn. Null whenever it could not be resolved — see
    /// <see cref="ClientLoaderInstall"/> — which is a worse pack rather than a broken one.
    /// </param>
    internal static IReadOnlyList<ModpackWriter.TextFile> Build(
        string serverName, ServerType type, string gameVersion, DateTime builtAtLocal,
        ClientLoaderInstall.Plan? loader = null)
    {
        // Composed here, never in the script: every placeholder is resolved by string.Format on this
        // side, so the templates only ever echo a literal and a variable of their own.
        var header = string.Format(Localizer.Get("Script_HeaderFmt"), serverName, gameVersion, type);
        var noLoader = string.Format(Localizer.Get("Script_NoLoaderFmt"), type, LoaderSite(type));

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HEADER"] = header,
            ["NO_LOADER"] = noLoader,
            ["LOADER_MARK"] = LoaderMark(type),
            ["STAMP"] = builtAtLocal.ToString("yyyyMMdd-HHmm"),
            ["CLOSE_GAME"] = Localizer.Get("Script_CloseGame"),
            ["NO_SOURCE"] = Localizer.Get("Script_NoSource"),
            ["ASK_FOLDER"] = Localizer.Get("Script_AskFolder"),
            ["DESTINATION_IS"] = Localizer.Get("Script_DestinationIs"),
            ["ASK_CONTINUE"] = Localizer.Get("Script_AskContinue"),
            ["CANCELLED"] = Localizer.Get("Script_Cancelled"),
            ["CANNOT_CREATE"] = Localizer.Get("Script_CannotCreate"),
            ["MOVE_FAILED"] = Localizer.Get("Script_MoveFailed"),
            ["MOVED_ASIDE"] = Localizer.Get("Script_MovedAside"),
            ["COPY_FAILED"] = Localizer.Get("Script_CopyFailed"),
            ["DONE"] = Localizer.Get("Script_Done"),
            ["HOW_TO_UNDO"] = Localizer.Get("Script_HowToUndo"),
            ["LAUNCHER_OFFICIAL"] = Localizer.Get("Script_LauncherOfficial"),
            ["ARROWS_HINT"] = Localizer.Get("Script_ArrowsHint"),
            ["OTHER_PATH"] = Localizer.Get("Script_OtherPath"),
            ["PICK_ONE"] = Localizer.Get("Script_PickOne"),
            ["TYPE_PATH"] = Localizer.Get("Script_TypePath"),
            ["ASK_INSTALL"] = Localizer.Get("Script_AskInstall"),
            ["CARRYING_ON"] = Localizer.Get("Script_CarryingOn"),
            ["DOWNLOADING"] = Localizer.Get("Script_Downloading"),
            ["DOWNLOAD_FAILED"] = Localizer.Get("Script_DownloadFailed"),
            ["NO_DOWNLOADER"] = Localizer.Get("Script_NoDownloader"),
            ["NO_JAVA"] = Localizer.Get("Script_NoJava"),
            ["NO_HASH_TOOL"] = Localizer.Get("Script_NoHashTool"),
            ["HASH_MISMATCH"] = Localizer.Get("Script_HashMismatch"),
            ["INSTALLING"] = Localizer.Get("Script_Installing"),
            ["LOADER_INSTALLED"] = Localizer.Get("Script_LoaderInstalled"),
            ["INSTALL_FAILED"] = Localizer.Get("Script_InstallFailed")
        };

        var yes = YesWords();

        // Shell and batch the builder wrote itself, never anything anybody translated. An empty URL
        // is how the templates are told there is nothing to offer.
        var wiring = new (string Token, string Raw)[]
        {
            ("INSTALLER_URL", loader?.Url ?? string.Empty),
            ("INSTALLER_SHA", loader?.Sha ?? string.Empty),
            ("INSTALLER_ARGS", loader?.Arguments ?? string.Empty),
            ("SHA_BITS", (loader?.Bits ?? 256).ToString()),
            ("SHA_TOOL", loader?.Bits == 160 ? "sha1sum" : "sha256sum"),
            ("SHA_ARGS", string.Empty),
            ("SHA_ALGO_WIN", loader?.Bits == 160 ? "SHA1" : "SHA256"),
            ("DEFAULT_YES", yes[0]),
            // choice.exe takes one key per option, in order: yes first, no second.
            ("YES_NO_KEYS", yes[0] + "n"),
            ("YES_PATTERN", string.Join("|", yes.SelectMany(w => new[] { w, w.ToUpperInvariant() }).Distinct()))
        };

        return new[]
        {
            new ModpackWriter.TextFile(
                WindowsName,
                Fill(Template("install-mods-windows.bat.in"), values, BatchSafe, wiring),
                Newline: "\r\n"),

            new ModpackWriter.TextFile(
                UnixName,
                Fill(Template("install-mods-unix.sh.in"), values, ShellSafe, wiring),
                // LF, and it is not a preference: a shell script with CRLF fails on its first line
                // with «$'\r': command not found», which to the player reads as a broken pack.
                Newline: "\n",
                Executable: true)
        };
    }

    /// <summary>The words that count as yes, plus the English ones, which cost nothing.</summary>
    private static List<string> YesWords() =>
        Localizer.Get("Script_YesWords").Split(',')
            .Select(w => w.Trim().ToLowerInvariant())
            .Concat(new[] { "y", "yes" })
            .Where(w => w.Length > 0 && w.All(char.IsAsciiLetter))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string Fill(
        string template,
        IReadOnlyDictionary<string, string> values,
        Func<string, string> escape,
        params (string Token, string Raw)[] verbatim)
    {
        foreach (var (token, text) in values)
            template = template.Replace($"@@{token}@@", escape(text), StringComparison.Ordinal);

        // Shell syntax the builder wrote itself, not text anybody translated.
        foreach (var (token, raw) in verbatim)
            template = template.Replace($"@@{token}@@", raw, StringComparison.Ordinal);

        return template;
    }

    /// <summary>
    /// A message made safe to sit inside double quotes in a shell script.
    /// </summary>
    /// <remarks>
    /// The messages are ours and the translations are in the repository, so this is not guarding
    /// against an attacker — it is guarding against an apostrophe. But a backtick or a <c>$</c> in
    /// a French string would be executed rather than printed, and that is a class of bug worth
    /// making impossible rather than remembering.
    /// </remarks>
    private static string ShellSafe(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>
    /// A message made safe to sit after <c>echo</c> in a batch file.
    /// </summary>
    /// <remarks>
    /// <c>%</c> would start a variable expansion, and the redirection characters would send the
    /// message to a file instead of the screen. <c>^</c> escapes them, and it has to go first or it
    /// would escape the carets added after it.
    /// </remarks>
    private static string BatchSafe(string text) =>
        text.Replace("^", "^^", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("&", "^&", StringComparison.Ordinal)
            .Replace("<", "^<", StringComparison.Ordinal)
            .Replace(">", "^>", StringComparison.Ordinal)
            .Replace("|", "^|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>The template as shipped, read out of the assembly.</summary>
    internal static string Template(string fileName)
    {
        var assembly = typeof(InstallScriptBuilder).GetTypeInfo().Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));

        if (name is null)
            throw new InvalidOperationException($"Embedded script template missing: {fileName}");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
