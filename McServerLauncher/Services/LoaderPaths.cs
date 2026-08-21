using System.IO;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Where each mod loader leaves the files the launcher has to find again afterwards.
/// </summary>
/// <remarks>
/// Forge and NeoForge install to the same shape and differ only in their maven coordinates. Three
/// separate places needed that path — the installer, the launcher and the detector — and each one
/// had Forge's spelled out inline. Adding a loader while missing one of them produces a server that
/// installs perfectly and then cannot be started, so the path lives here once instead.
/// </remarks>
public static class LoaderPaths
{
    /// <summary>
    /// The directory under the server folder holding one subdirectory per installed loader version,
    /// or null for loaders launched with a plain <c>-jar</c> (Vanilla, Fabric, old Forge).
    /// </summary>
    public static string? LibrariesRoot(string serverFolder, ServerType type) => type switch
    {
        ServerType.Forge => Path.Combine(serverFolder, "libraries", "net", "minecraftforge", "forge"),
        ServerType.NeoForge => Path.Combine(serverFolder, "libraries", "net", "neoforged", "neoforge"),
        _ => null
    };

    /// <summary>The args file inside a version directory. Windows and Unix get different ones.</summary>
    public static string ArgsFileName =>
        OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt";

    /// <summary>Every loader that launches through an args file rather than a runnable jar.</summary>
    public static readonly ServerType[] ArgsFileLoaders = { ServerType.Forge, ServerType.NeoForge };
}
