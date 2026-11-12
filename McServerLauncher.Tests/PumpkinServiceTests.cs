using System.Runtime.InteropServices;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Choosing and verifying a Pumpkin release, from what its GitHub release really looks like.
/// </summary>
/// <remarks>
/// The checksum file and the asset names are those of release <c>0.2.2+26.3-26.60</c>, published on
/// 2026-11-08. No test here touches the network.
/// </remarks>
public class PumpkinServiceTests
{
    /// <summary>The release's <c>checksums.sha256</c>, as published.</summary>
    private const string Checksums = """
        9882aa350ee8674d12870ee7c9fa6eed387101c32c34a5f3071a9040b1c29fd3  pumpkin-ARM64-Linux
        38748e5f211ef4a6e3191056b1c7b2663d549a978291b34708b49634f4990bf1  pumpkin-ARM64-Windows.exe
        b9fc3db0dfd41867c60abedc901539ec08da54454d2e8550fbd8e43a1c3447e1  pumpkin-ARM64-macOS
        3755df414cdece664223388a93421c5c674682f5b9264387416b8a9074cd8d59  pumpkin-X64-Linux
        9a0370da82dd156b88421e74bc87deebdb13601a79b67ef3de0b64e2baae876d  pumpkin-X64-Windows.exe
        b2d82f6b22014af45153a209a233b9cedf77cce905bdc042d72fe7f6decb1fe2  pumpkin-X64-v3-Linux
        d85fe9829bf6fa7d36c87f127eb3de4c7d936a715c5b016edc79af0a6f1361ae  pumpkin-X64-v3-Windows.exe
        77a0fc7f2ec01646b849524509043ba884a246a7687a838c78a7bfb40b171ab1  pumpkin-aarch64-android
        """;

    [Theory]
    [InlineData(PumpkinService.Os.Windows, Architecture.X64, "pumpkin-X64-Windows.exe")]
    [InlineData(PumpkinService.Os.Windows, Architecture.Arm64, "pumpkin-ARM64-Windows.exe")]
    [InlineData(PumpkinService.Os.Linux, Architecture.X64, "pumpkin-X64-Linux")]
    [InlineData(PumpkinService.Os.Linux, Architecture.Arm64, "pumpkin-ARM64-Linux")]
    [InlineData(PumpkinService.Os.MacOs, Architecture.Arm64, "pumpkin-ARM64-macOS")]
    [InlineData(PumpkinService.Os.MacOs, Architecture.X64, "pumpkin-X64-macOS")]
    public void EachSystemGetsItsOwnPlainBuild(PumpkinService.Os os, Architecture arch, string expected)
    {
        // Never the -v3 builds: they need AVX2 and die with an illegal instruction without it.
        Assert.Equal(expected, PumpkinService.AssetNameFor(os, arch));
    }

    [Theory]
    [InlineData(PumpkinService.Os.Windows, Architecture.X86)]
    [InlineData(PumpkinService.Os.Linux, Architecture.Arm)]
    public void ASystemPumpkinDoesNotBuildForGetsNothing(PumpkinService.Os os, Architecture arch)
    {
        Assert.Null(PumpkinService.AssetNameFor(os, arch));
        Assert.Null(PumpkinService.AssetNameFor(null, Architecture.X64));
    }

    [Fact]
    public void TheHashIsTheOneForExactlyThatFile()
    {
        Assert.Equal("9a0370da82dd156b88421e74bc87deebdb13601a79b67ef3de0b64e2baae876d",
            PumpkinService.ChecksumFor(Checksums, "pumpkin-X64-Windows.exe"));

        // "pumpkin-X64-Linux" is also how "pumpkin-X64-Linux-musl" and nothing here starts, but
        // "pumpkin-X64" is how four of them do: a prefix match would verify one file with
        // another's hash, or — worse — accept the first line it met.
        Assert.Equal("3755df414cdece664223388a93421c5c674682f5b9264387416b8a9074cd8d59",
            PumpkinService.ChecksumFor(Checksums, "pumpkin-X64-Linux"));
        Assert.Null(PumpkinService.ChecksumFor(Checksums, "pumpkin-X64"));
    }

    [Fact]
    public void AFileTheReleaseDoesNotListHasNoHash()
    {
        // 0.2.2 ships no Intel macOS build. No hash means the install is refused, not unchecked.
        Assert.Null(PumpkinService.ChecksumFor(Checksums, "pumpkin-X64-macOS"));
        Assert.Null(PumpkinService.ChecksumFor(string.Empty, "pumpkin-X64-Windows.exe"));
    }

    [Fact]
    public void TheChecksumFileIsReadWhateverItsLineEndingsAndMarkers()
    {
        // sha256sum's binary-mode marker, and a file that came through Windows.
        const string sums = "9A0370DA82DD156B88421E74BC87DEEBDB13601A79B67EF3DE0B64E2BAAE876D *pumpkin.exe\r\n";

        Assert.Equal("9a0370da82dd156b88421e74bc87deebdb13601a79b67ef3de0b64e2baae876d",
            PumpkinService.ChecksumFor(sums, "pumpkin.exe"));
    }

    [Theory]
    [InlineData("0.2.2+26.3-26.60", "0.2.2", "26.3")]
    [InlineData("0.2.1+26.3-26.51", "0.2.1", "26.3")]
    [InlineData("0.1.0-dev+26.2-26.45", "0.1.0-dev", "26.2")]
    [InlineData("1.0.0+1.21.11-1.21.130", "1.0.0", "1.21.11")]
    public void TheTagSaysWhichMinecraftItIs(string tag, string version, string game)
    {
        Assert.Equal((version, game), PumpkinService.ParseTag(tag));
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("v0.3.0")]
    public void ATagInAnotherShapeKeepsItsNameAndClaimsNoVersion(string tag)
    {
        // An empty game version is honest: the card cannot say which Minecraft it is. A guess
        // would be shown as fact.
        Assert.Equal((tag, string.Empty), PumpkinService.ParseTag(tag));
    }

    [Fact]
    public void AReleaseIsItsTagAndItsFiles()
    {
        const string json = """
            {
              "tag_name": "0.2.2+26.3-26.60",
              "prerelease": false,
              "assets": [
                { "name": "checksums.sha256",
                  "browser_download_url": "https://github.com/Pumpkin-MC/Pumpkin/releases/download/0.2.2%2B26.3-26.60/checksums.sha256" },
                { "name": "pumpkin-X64-Windows.exe",
                  "browser_download_url": "https://github.com/Pumpkin-MC/Pumpkin/releases/download/0.2.2%2B26.3-26.60/pumpkin-X64-Windows.exe" },
                { "name": "nameless" }
              ]
            }
            """;

        var (tag, assets) = PumpkinService.ParseRelease(json);

        Assert.Equal("0.2.2+26.3-26.60", tag);
        Assert.Equal(2, assets.Count);
        Assert.EndsWith("/pumpkin-X64-Windows.exe", assets["pumpkin-X64-Windows.exe"]);
        Assert.True(AppHttp.IsAllowedUrl(assets["pumpkin-X64-Windows.exe"], "github.com"));
    }

    [Fact]
    public void AnAnswerWithNoTagIsAFailureNotAnEmptyRelease()
    {
        Assert.Throws<UserFacingException>(() => PumpkinService.ParseRelease("""{ "message": "Not Found" }"""));
    }
}
