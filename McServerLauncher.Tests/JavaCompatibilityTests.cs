using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Which installed Java a server may run on, and which one to download when none fits.
/// </summary>
/// <remarks>
/// Minecraft 1.17 and 1.17.1 declare Java 16, and Adoptium publishes no Java 16 at all (checked
/// against its API: <c>/v3/assets/latest/16/hotspot</c> answers <c>[]</c>). With 16 only accepting
/// 16, a 1.17 server could neither use a Java 17 already installed nor download a 16, and failed to
/// start with "No Java 16 download was found".
/// </remarks>
public class JavaCompatibilityTests
{
    [Theory]
    [InlineData(8, 8, true)]
    [InlineData(17, 8, false)]     // old servers want exactly 8
    [InlineData(16, 16, true)]
    [InlineData(17, 16, true)]     // the fix: 1.17.x runs on 17
    [InlineData(21, 16, false)]    // nothing promises 1.17 runs on anything later
    [InlineData(21, 17, true)]
    [InlineData(17, 21, false)]
    public void WhichInstalledJavaFits(int installed, int required, bool fits) =>
        Assert.Equal(fits, JavaService.IsCompatible(installed, required));

    [Theory]
    [InlineData(8, 8)]
    [InlineData(16, 17)]
    [InlineData(17, 17)]
    [InlineData(21, 21)]
    public void WhatGetsDownloaded(int required, int downloaded) =>
        Assert.Equal(downloaded, JavaService.DownloadableMajor(required));

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(21)]
    [InlineData(25)]
    public void WhatGetsDownloadedAlwaysFits(int required) =>
        Assert.True(JavaService.IsCompatible(JavaService.DownloadableMajor(required), required));
}
