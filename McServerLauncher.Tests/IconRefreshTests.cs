using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Keeping the Linux desktop icon in step with the running build.
/// </summary>
/// <remarks>
/// The guarantee worth protecting here is the negative one. This runs unattended on every single
/// startup, so if it ever created an icon instead of refreshing one it would be putting the app on
/// the desktop of people who never asked for it.
/// </remarks>
public class IconRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-icon-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] NewIcon = { 1, 2, 3, 4, 5, 6, 7, 8 };
    private static readonly byte[] OldIcon = { 9, 9, 9, 9, 9, 9, 9, 9 };   // same length, different bytes

    public IconRefreshTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private string Source => Path.Combine(_dir, "new.png");
    private string Installed => Path.Combine(_dir, "installed.png");

    [Fact]
    public void WithNoIconInstalledItDoesNothingAtAll()
    {
        File.WriteAllBytes(Source, NewIcon);

        Assert.False(DesktopShortcutService.RefreshIconFrom(Source, Installed));

        // The part that matters: it must not have created one.
        Assert.False(File.Exists(Installed));
    }

    [Fact]
    public void WithNothingToCopyFromItLeavesTheExistingOneAlone()
    {
        File.WriteAllBytes(Installed, OldIcon);

        Assert.False(DesktopShortcutService.RefreshIconFrom(Path.Combine(_dir, "missing.png"), Installed));
        Assert.Equal(OldIcon, File.ReadAllBytes(Installed));
    }

    [Fact]
    public void ADifferentIconReplacesTheInstalledOne()
    {
        File.WriteAllBytes(Source, NewIcon);
        File.WriteAllBytes(Installed, OldIcon);

        Assert.True(DesktopShortcutService.RefreshIconFrom(Source, Installed));
        Assert.Equal(NewIcon, File.ReadAllBytes(Installed));
    }

    [Fact]
    public void AnIconThatAlreadyMatchesIsNotRewritten()
    {
        // Otherwise every launch would rewrite the file and poke the icon cache for nothing.
        File.WriteAllBytes(Source, NewIcon);
        File.WriteAllBytes(Installed, NewIcon);

        Assert.False(DesktopShortcutService.RefreshIconFrom(Source, Installed));
    }

    [Fact]
    public void SameSizeButDifferentContentStillCountsAsChanged()
    {
        // Comparing only lengths would let a redesigned icon that happens to weigh the same slip
        // through for ever — and icons of a fixed 256×256 PNG often do land on similar sizes.
        File.WriteAllBytes(Source, NewIcon);
        File.WriteAllBytes(Installed, OldIcon);

        Assert.Equal(NewIcon.Length, OldIcon.Length);
        Assert.True(DesktopShortcutService.RefreshIconFrom(Source, Installed));
    }

    [Fact]
    public void ADifferentSizeCountsAsChangedToo()
    {
        File.WriteAllBytes(Source, NewIcon);
        File.WriteAllBytes(Installed, new byte[] { 1, 2, 3 });

        Assert.True(DesktopShortcutService.RefreshIconFrom(Source, Installed));
        Assert.Equal(NewIcon, File.ReadAllBytes(Installed));
    }
}
