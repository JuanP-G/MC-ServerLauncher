using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// server.properties is rewritten through a temporary file, keeping everything it did not change.
/// </summary>
/// <remarks>
/// It was written in place, so a power cut halfway through left the server a truncated file. The
/// other files the app owns already went through a temporary one.
/// </remarks>
public class PropertiesWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-props-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "server.properties");

    public PropertiesWriteTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void AnUpdateKeepsTheRestAndLeavesNoTemporaryFile()
    {
        File.WriteAllText(File_, "#Minecraft server properties\nmotd=Hola\nserver-port=25565\n");

        new ServerPropertiesService().Update(File_, new Dictionary<string, string> { ["server-port"] = "25570" });

        var lines = File.ReadAllLines(File_);
        Assert.Equal(new[] { "#Minecraft server properties", "motd=Hola", "server-port=25570" }, lines);
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
