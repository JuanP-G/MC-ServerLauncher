using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// whitelist.json keeps the lower-case keys Minecraft reads, whatever the properties are called here.
/// </summary>
public class WhitelistFileFormatTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "mcl-wlformat-" + Guid.NewGuid().ToString("N"));

    public WhitelistFileFormatTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task TheFileIsWrittenWithTheKeysTheServerReads()
    {
        await new WhitelistService().AddAsync(_folder, "Alex", onlineMode: false);

        var json = File.ReadAllText(Path.Combine(_folder, "whitelist.json"));

        Assert.Contains("\"uuid\":", json);
        Assert.Contains("\"name\": \"Alex\"", json);
        Assert.DoesNotContain("\"Uuid\"", json);
        Assert.DoesNotContain("\"Name\"", json);
    }

    [Fact]
    public void AFileTheServerWroteIsRead()
    {
        File.WriteAllText(Path.Combine(_folder, "whitelist.json"),
            """[{"uuid":"11111111-2222-3333-4444-555555555555","name":"Steve"}]""");

        Assert.Equal(new[] { "Steve" }, new WhitelistService().ReadNames(_folder));
    }
}
