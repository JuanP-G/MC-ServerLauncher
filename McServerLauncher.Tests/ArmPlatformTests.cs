using System.Runtime.InteropServices;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// ARM machines get what they can run, not only what is native.
/// </summary>
/// <remarks>
/// The README promises Windows on ARM through x64 emulation, and on those machines the Java
/// download asked Adoptium for aarch64 only — there is none for Java 8, 16 or 17 on Windows, nor for
/// Java 8 on macOS — and the Playit agent was reported unsupported. Both now fall back to x64 where
/// the system runs it.
/// </remarks>
public class ArmPlatformTests
{
    [Fact]
    public void WindowsAndMacOnArmTryNativeJavaThenX64() =>
        Assert.Equal(new[] { "aarch64", "x64" },
            JavaService.AdoptiumArchitectures(Architecture.Arm64, emulatesX64: true));

    [Fact]
    public void LinuxOnArmOnlyGetsArmJava() =>
        Assert.Equal(new[] { "aarch64" },
            JavaService.AdoptiumArchitectures(Architecture.Arm64, emulatesX64: false));

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.X86, "x86")]
    public void OtherMachinesAskForTheirOwn(Architecture arch, string asked) =>
        Assert.Equal(new[] { asked }, JavaService.AdoptiumArchitectures(arch, emulatesX64: true));

    [Theory]
    [InlineData(true, false, Architecture.X64, "playit-windows-x86_64-signed.exe")]
    [InlineData(true, false, Architecture.Arm64, "playit-windows-x86_64-signed.exe")]
    [InlineData(true, false, Architecture.X86, "playit-windows-x86-signed.exe")]
    [InlineData(false, true, Architecture.X64, "playit-linux-amd64")]
    [InlineData(false, true, Architecture.Arm64, "playit-linux-aarch64")]
    [InlineData(false, false, Architecture.Arm64, null)]   // macOS: Playit ships no binary
    public void ThePlayitAgentForEachPlatform(bool windows, bool linux, Architecture arch, string? asset) =>
        Assert.Equal(asset, PlayitAgentRunner.AssetFor(windows, linux, arch));
}
