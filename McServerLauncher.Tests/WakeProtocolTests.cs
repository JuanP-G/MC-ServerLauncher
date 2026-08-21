using System.Text.Json;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The slice of the Minecraft protocol the sleeping-server listener speaks. Behind a Playit tunnel
/// every byte of this arrives from the open internet, so the hostile inputs matter as much as the
/// well-formed ones.
/// </summary>
public class WakeProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(767)]
    [InlineData(25565)]
    [InlineData(2097151)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void VarIntSurvivesARoundTrip(int value)
    {
        using var buffer = new MemoryStream();
        WakeOnDemandListener.WriteVarInt(buffer, value);
        buffer.Position = 0;

        Assert.Equal(value, WakeOnDemandListener.ReadVarInt(buffer));
    }

    [Fact]
    public void StringsKeepTheirAccents()
    {
        using var buffer = new MemoryStream();
        WakeOnDemandListener.WriteString(buffer, "Servidor de Ñoño");
        buffer.Position = 0;

        Assert.Equal("Servidor de Ñoño", WakeOnDemandListener.ReadString(buffer));
    }

    [Fact]
    public void AVarIntThatNeverEndsIsRefused()
    {
        // Every byte with the continuation bit set: without the five-byte cap this spins for ever
        // on whatever the peer feels like sending.
        using var buffer = new MemoryStream(Enumerable.Repeat((byte)0x80, 64).ToArray());

        Assert.Null(WakeOnDemandListener.TryReadVarInt(buffer));
    }

    [Fact]
    public void ATruncatedVarIntIsRefusedRatherThanGuessed()
    {
        using var buffer = new MemoryStream(new byte[] { 0x80 });   // continues, then nothing

        Assert.Null(WakeOnDemandListener.TryReadVarInt(buffer));
    }

    [Fact]
    public void AbsurdStringLengthAllocatesNothing()
    {
        // A length prefix claiming far more than any real field, with no payload behind it. The
        // answer must be an empty string, not a 2 GB allocation attempt.
        using var buffer = new MemoryStream();
        WakeOnDemandListener.WriteVarInt(buffer, int.MaxValue);
        buffer.Position = 0;

        Assert.Equal(string.Empty, WakeOnDemandListener.ReadString(buffer));
    }

    [Fact]
    public void StatusEchoesTheProtocolTheClientAskedFor()
    {
        // Answering with a fixed protocol number makes the client paint the server as incompatible
        // and refuse to let anyone press Join — which would defeat the entire feature.
        var status = new WakeStatus("Mi servidor\nApagado", "1.21.1", 8, null, "encendiendo");

        using var doc = JsonDocument.Parse(WakeOnDemandListener.BuildStatusJson(status, 767, null));
        var root = doc.RootElement;

        Assert.Equal(767, root.GetProperty("version").GetProperty("protocol").GetInt32());
        Assert.Equal("1.21.1", root.GetProperty("version").GetProperty("name").GetString());
        Assert.Equal(0, root.GetProperty("players").GetProperty("online").GetInt32());
        Assert.Equal(8, root.GetProperty("players").GetProperty("max").GetInt32());
        Assert.Contains("Apagado", root.GetProperty("description").GetProperty("text").GetString());
    }

    [Fact]
    public void ColourCodesAndNewlinesReachTheClientIntact()
    {
        // The notice carries § colour codes and sits on the second line; JSON encoding must not
        // mangle either, or it comes out as literal escapes in the server list.
        var status = new WakeStatus("Mi servidor\n§r§e§lApagado", "1.21.1", 8, null, "x");

        using var doc = JsonDocument.Parse(WakeOnDemandListener.BuildStatusJson(status, 767, null));
        var description = doc.RootElement.GetProperty("description").GetProperty("text").GetString();

        Assert.Contains("§r§e§l", description);
        Assert.Contains("\n", description);
    }
}
