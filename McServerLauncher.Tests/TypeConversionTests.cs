using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Changing a server's type: what happens to the content it already had.
/// </summary>
/// <remarks>
/// Converting a NeoForge server to Paper left twenty-odd mod jars in <c>mods/</c>. Paper never
/// reads that folder, so the server had silently lost all its content while the app still showed a
/// full list — the tab named after the new family, the jars underneath belonging to the old one.
/// </remarks>
public class TypeConversionTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-convert-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTime Stamp = new(2026, 8, 30, 14, 5, 9);

    private string Content(ServerType type, params string[] jars)
    {
        var dir = Path.Combine(_folder, ServerTypeCatalog.ContentFolder(type));
        Directory.CreateDirectory(dir);
        foreach (var jar in jars) File.WriteAllText(Path.Combine(dir, jar), "");
        return dir;
    }

    [Fact]
    public void ModsAreMovedAsideWhenBecomingAPluginServer()
    {
        Content(ServerType.NeoForge, "Geyser-Neoforge.jar", "waystones.jar");

        var archived = ContentMigrationService.ArchiveIfFamilyChanged(
            _folder, ServerType.NeoForge, ServerType.Paper, Stamp);

        Assert.Equal("mods-neoforge-20260830-140509", archived);
        Assert.False(Directory.Exists(Path.Combine(_folder, "mods")));

        // Moved, not deleted: converting back has to find them, and the app has no business
        // destroying a folder somebody spent an evening filling.
        var kept = Directory.GetFiles(Path.Combine(_folder, archived!)).Select(Path.GetFileName).ToArray();
        Assert.Equal(2, kept.Length);
        Assert.Contains("waystones.jar", kept);
    }

    [Fact]
    public void PluginsAreMovedAsideWhenBecomingAModServer()
    {
        Content(ServerType.Paper, "Geyser-Spigot.jar");

        var archived = ContentMigrationService.ArchiveIfFamilyChanged(
            _folder, ServerType.Paper, ServerType.Fabric, Stamp);

        Assert.StartsWith("plugins-paper-", archived);
        Assert.False(Directory.Exists(Path.Combine(_folder, "plugins")));
    }

    [Fact]
    public void StayingInTheSameFamilyLeavesTheContentAlone()
    {
        // Paper to Purpur keeps every plugin, and Fabric to NeoForge keeps the mods folder. Some
        // jars may not work afterwards, but that is the user's call and the dialog already warns —
        // moving them would take away a working server's content for no reason.
        Content(ServerType.Paper, "essentials.jar");
        Assert.Null(ContentMigrationService.ArchiveIfFamilyChanged(
            _folder, ServerType.Paper, ServerType.Purpur, Stamp));
        Assert.True(File.Exists(Path.Combine(_folder, "plugins", "essentials.jar")));

        Content(ServerType.Fabric, "lithium.jar");
        Assert.Null(ContentMigrationService.ArchiveIfFamilyChanged(
            _folder, ServerType.Fabric, ServerType.NeoForge, Stamp));
        Assert.True(File.Exists(Path.Combine(_folder, "mods", "lithium.jar")));
    }

    [Fact]
    public void NothingHappensWhenThereIsNothingToMove()
    {
        Directory.CreateDirectory(_folder);

        // No folder at all.
        Assert.Null(ContentMigrationService.ArchiveIfFamilyChanged(
            _folder, ServerType.NeoForge, ServerType.Paper, Stamp));

        // A folder holding only what a loader leaves behind is not content the user installed;
        // archiving it would put an empty "mods-neoforge-…" beside every converted server.
        var mods = Content(ServerType.NeoForge);
        Directory.CreateDirectory(Path.Combine(mods, "cache"));
        Assert.Null(ContentMigrationService.ArchiveIfFamilyChanged(
            _folder, ServerType.NeoForge, ServerType.Paper, Stamp));
        Assert.True(Directory.Exists(mods));
    }

    [Fact]
    public void ComingFromVanillaMovesNothing()
    {
        // Vanilla has no content folder of its own, and a mods/ directory sitting next to a vanilla
        // server was put there by hand — converting to Fabric is the moment it starts working.
        Content(ServerType.NeoForge, "somebody-put-this-here.jar");

        Assert.Null(ContentMigrationService.ArchiveIfFamilyChanged(
            _folder, ServerType.Vanilla, ServerType.Fabric, Stamp));
        Assert.True(File.Exists(Path.Combine(_folder, "mods", "somebody-put-this-here.jar")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
