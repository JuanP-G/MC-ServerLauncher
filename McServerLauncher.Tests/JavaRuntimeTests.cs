using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What <c>java -version</c> says about the VM, beyond its number: the memory profile gives OpenJ9
/// only the heap size, and caps a 32-bit Java's heap.
/// </summary>
/// <remarks>The texts are what these Javas really print, copied from the machines they ran on.</remarks>
public class JavaRuntimeTests
{
    [Fact]
    public void TemurinIsA64BitHotSpot()
    {
        var runtime = JavaService.ParseRuntime("""
            openjdk version "25.0.3" 2026-04-21 LTS
            OpenJDK Runtime Environment Temurin-25.0.3+9 (build 25.0.3+9-LTS)
            OpenJDK 64-Bit Server VM Temurin-25.0.3+9 (build 25.0.3+9-LTS, mixed mode, sharing)
            """);

        Assert.Equal(new JavaService.JavaRuntime(25, IsOpenJ9: false, Is64Bit: true), runtime);
    }

    [Fact]
    public void SemeruIsOpenJ9()
    {
        var runtime = JavaService.ParseRuntime("""
            openjdk version "25.0.4.1" 2026-08-18 LTS
            IBM Semeru Runtime Open Edition 25.0.4.15 (build 25.0.4.1+1-LTS)
            Eclipse OpenJ9 VM 25.0.4.15 (build 25.0.4.1+1-openj9-0.61.0, JRE 25 Windows 11 amd64-64-Bit Compressed References 20260818_338 (JIT enabled, AOT enabled)
            """);

        Assert.Equal(new JavaService.JavaRuntime(25, IsOpenJ9: true, Is64Bit: true), runtime);
    }

    [Fact]
    public void Java8IsReadFromItsOldNumbering()
    {
        var runtime = JavaService.ParseRuntime("""
            java version "1.8.0_481"
            Java(TM) SE Runtime Environment (build 1.8.0_481-b10)
            Java HotSpot(TM) 64-Bit Server VM (build 25.481-b10, mixed mode)
            """);

        Assert.Equal(new JavaService.JavaRuntime(8, IsOpenJ9: false, Is64Bit: true), runtime);
    }

    [Fact]
    public void AVmLineWithout64BitIsA32BitJava()
    {
        var runtime = JavaService.ParseRuntime("""
            java version "1.8.0_481"
            Java(TM) SE Runtime Environment (build 1.8.0_481-b10)
            Java HotSpot(TM) Client VM (build 25.481-b10, mixed mode, sharing)
            """);

        Assert.False(runtime.Is64Bit);
    }

    [Fact]
    public void OutputWithNoVmLineIsNotTakenFor32Bit()
    {
        // Capping a heap on a guess would do more harm than not capping it.
        var runtime = JavaService.ParseRuntime("openjdk version \"21.0.2\" 2024-01-16");

        Assert.Equal(21, runtime.Major);
        Assert.True(runtime.Is64Bit);
    }

    [Fact]
    public void SomethingThatIsNotJavaIsUnknown()
    {
        Assert.Equal(JavaService.JavaRuntime.Unknown, JavaService.ParseRuntime("'java' no se reconoce"));
    }
}
