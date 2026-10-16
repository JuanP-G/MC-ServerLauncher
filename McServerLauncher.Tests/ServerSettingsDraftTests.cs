using McServerLauncher.Models;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// Saving the settings page either reaches the server or leaves it as it was.
/// </summary>
/// <remarks>
/// The draft copied the changed fields into the live config before writing <c>server.properties</c>.
/// A server whose folder had gone missing — which the page allows on purpose, so its difficulty can
/// still be changed — failed on the file with the new name and RAM already on the live config: the
/// server list showed them, Discard only put the page back, and the next save of <c>servers.json</c>
/// made them permanent.
/// </remarks>
public class ServerSettingsDraftTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-draft-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ASaveThatCannotWriteItsFilesLeavesTheServerAsItWas()
    {
        var live = new ServerConfig { Name = "survival", FolderPath = Path.Combine(_root, "gone"), MaxRamGb = 4 };
        var draft = new ServerSettingsDraft(live);
        draft.Track("difficulty", "easy");

        draft.Config.Name = "renamed";
        draft.Config.MaxRamGb = 8;
        draft.Set("difficulty", "hard");

        Assert.ThrowsAny<IOException>(() => draft.Apply());
        Assert.Equal("survival", live.Name);
        Assert.Equal(4, live.MaxRamGb);

        draft.Revert();
        Assert.Equal("survival", draft.Config.Name);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void AFileAlreadyWrittenCountsAsSavedWhenALaterOneFails()
    {
        Directory.CreateDirectory(_root);
        var live = new ServerConfig { Name = "survival", FolderPath = _root };
        var draft = new ServerSettingsDraft(live);
        draft.Track("difficulty", "easy");
        draft.Set("difficulty", "hard");

        // The icon cannot be written: a folder sits where the file would go.
        Directory.CreateDirectory(Path.Combine(_root, "server-icon.png"));
        draft.UseIcon([1, 2, 3]);

        Assert.ThrowsAny<Exception>(() => draft.Apply());

        Assert.Contains("difficulty=hard", File.ReadAllText(live.PropertiesPath));
        Assert.False(draft.IsPageDirty(ServerSettingsPage.Game));         // on disk, so not unsaved
        Assert.True(draft.IsPageDirty(ServerSettingsPage.Appearance));    // the icon still is
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
