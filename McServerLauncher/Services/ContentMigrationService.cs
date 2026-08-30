using System.IO;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What happens to the mods or plugins already installed when a server changes family.
/// </summary>
/// <remarks>
/// <para>
/// Converting a NeoForge server to Paper leaves twenty-odd mod jars sitting in <c>mods/</c>. Paper
/// never reads that folder, so they are dead weight — but they are not harmless: the Mods tab is
/// named after the new family while the jars underneath belong to the old one, and the server looks
/// like it kept its content when it has silently lost all of it.
/// </para>
/// <para>
/// They are moved aside rather than deleted. Someone converting to try Paper out may well convert
/// back, and the app has no business destroying a folder the user spent an evening filling.
/// </para>
/// </remarks>
public static class ContentMigrationService
{
    /// <summary>
    /// Moves the old family's content folder aside when the type change crosses families.
    /// </summary>
    /// <param name="folder">The server folder.</param>
    /// <param name="from">The type the server was.</param>
    /// <param name="to">The type it is becoming.</param>
    /// <param name="stamp">Used to name the archive; taken as an argument so tests are not timing-dependent.</param>
    /// <returns>The name of the folder the content was moved to, or null when nothing was moved.</returns>
    /// <remarks>
    /// Does nothing when the family is unchanged (Paper to Purpur keeps its plugins, Fabric to
    /// NeoForge keeps its mods — the jars may not all work, but that is the user's call and the
    /// dialog already warns about it). Also does nothing when the folder is empty or absent.
    /// </remarks>
    public static string? ArchiveIfFamilyChanged(string folder, ServerType from, ServerType to, DateTime stamp)
    {
        var oldFamily = ServerTypeCatalog.For(from).Family;
        var newFamily = ServerTypeCatalog.For(to).Family;
        if (oldFamily == newFamily || oldFamily == ServerFamily.None) return null;

        var oldName = ServerTypeCatalog.ContentFolder(from);
        var source = Path.Combine(folder, oldName);
        if (!Directory.Exists(source)) return null;

        // An empty folder is not worth archiving, and neither is one holding only the subfolders a
        // loader leaves behind — only actual jars are content the user chose to install.
        var hasJars = Directory.EnumerateFiles(source)
            .Any(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                   || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));
        if (!hasJars) return null;

        var archive = $"{oldName}-{from.ToString().ToLowerInvariant()}-{stamp:yyyyMMdd-HHmmss}";
        var target = Path.Combine(folder, archive);

        // Absurd in practice (the stamp goes to the second), but a collision would throw mid-convert
        // and leave the server half-changed, which is a worse outcome than an uglier name.
        var suffix = 1;
        while (Directory.Exists(target))
            target = Path.Combine(folder, archive + "-" + suffix++);

        Directory.Move(source, target);
        return Path.GetFileName(target);
    }
}
