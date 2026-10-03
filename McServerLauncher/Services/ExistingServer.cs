using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What the user settled on in the create dialog for a folder that already holds a server.</summary>
/// <remarks>
/// <c>Type</c>, <c>GameVersion</c> and <c>JarFile</c> are what was picked by hand, and count only
/// where the folder itself said nothing: what was detected always wins.
/// </remarks>
public sealed record ExistingServerForm(
    string Name,
    string Folder,
    ServerType Type,
    string? GameVersion,
    string? JarFile,
    int MinRamGb,
    int MaxRamGb,
    string JavaPath,
    bool Playit,
    bool Crossplay,
    bool MultiVersion,
    bool Hydraulic);

/// <summary>
/// Turns a folder that already holds a server into one the app manages, without touching it.
/// </summary>
/// <remarks>
/// <para>
/// This is what took the place of the old "Add" button: the create dialog's "use a folder that
/// already exists". What the folder says wins over what the form says — the type and version it was
/// detected as are shown locked — and the form fills in only what could not be told from the files.
/// </para>
/// <para>
/// <strong>Nothing here writes to the folder</strong>, with one deliberate exception in
/// <see cref="ApplyPort"/>: no download, no installer, no <c>eula.txt</c>, no <c>run.bat</c>. A
/// server someone already has is theirs, and "add it to the app" must never turn into "reinstall it
/// over itself".
/// </para>
/// </remarks>
public static class ExistingServer
{
    /// <summary>Why this cannot be added, as a resx key; null when it can.</summary>
    public static string? Problem(ExistingServerForm form, ServerDetection found, IEnumerable<string> registeredFolders)
    {
        if (string.IsNullOrWhiteSpace(form.Folder) || !Directory.Exists(form.Folder)) return "Cs_ExistingFolderMissing";
        if (string.IsNullOrWhiteSpace(form.Name)) return "Msg_NameRequired";
        if (registeredFolders.Any(f => SameFolder(f, form.Folder))) return "Cs_ExistingAlreadyAdded";

        var version = found.GameVersion ?? form.GameVersion;
        if (string.IsNullOrWhiteSpace(version)) return "Cs_ExistingNeedsVersion";

        // Something to launch: an args file for the loaders that use one, a jar for the rest.
        var jar = found.JarFile ?? form.JarFile;
        if (string.IsNullOrEmpty(found.ForgeArgs)
            && (string.IsNullOrWhiteSpace(jar) || !File.Exists(Path.Combine(form.Folder, jar))))
            return "Cs_ExistingNeedsJar";

        if (form.MaxRamGb < form.MinRamGb) return "Msg_RamMaxMin";
        return null;
    }

    /// <summary>The config for the app to manage, from what was found and what was chosen.</summary>
    public static ServerConfig ToConfig(ExistingServerForm form, ServerDetection found)
    {
        var type = found.Type ?? form.Type;
        var config = new ServerConfig
        {
            Name = form.Name.Trim(),
            FolderPath = form.Folder,
            Type = type,
            GameVersion = found.GameVersion ?? form.GameVersion ?? string.Empty,
            JavaPath = form.JavaPath,
            MinRamGb = form.MinRamGb,
            MaxRamGb = form.MaxRamGb,
            PlayitEnabled = form.Playit,
            // Each only if this type can do it: the checkbox is greyed out for the rest, but the
            // config must not be able to say otherwise either.
            CrossplayEnabled = form.Crossplay && CrossplayService.CanEnable(type),
            MultiVersionEnabled = form.MultiVersion && MultiVersionService.CanEnable(type),
            BedrockModContentEnabled = form.Hydraulic && HydraulicService.CanEnable(type)
        };

        if (!string.IsNullOrEmpty(found.LoaderVersion)) config.ModLoaderVersion = found.LoaderVersion;
        if (!string.IsNullOrEmpty(found.ForgeArgs)) config.ForgeArgs = found.ForgeArgs;
        var jar = found.JarFile ?? form.JarFile;
        if (!string.IsNullOrWhiteSpace(jar)) config.JarFile = jar;
        return config;
    }

    /// <summary>
    /// Writes the port into server.properties if, and only if, it is not what the folder already says.
    /// </summary>
    /// <remarks>
    /// The one thing the dialog ever writes into an existing server, and only because otherwise the
    /// port it shows would be a lie: the server reads its port from that file, not from the app. Only
    /// that key is touched; the rest of the file, its comments and its order stay as they were.
    /// </remarks>
    /// <returns>Whether anything was written.</returns>
    public static bool ApplyPort(string folder, int? detectedPort, int chosenPort)
    {
        if (detectedPort == chosenPort) return false;
        new ServerPropertiesService().Update(Path.Combine(folder, "server.properties"),
            new Dictionary<string, string> { ["server-port"] = chosenPort.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return true;
    }

    /// <summary>Whether two paths name the same folder, however they were typed.</summary>
    public static bool SameFolder(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            static string Norm(string p) =>
                Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Norm(a), Norm(b),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
