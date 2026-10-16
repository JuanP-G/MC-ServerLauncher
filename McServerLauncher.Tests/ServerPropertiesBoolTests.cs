using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A true/false key of server.properties, read once and the way the server reads it.
/// </summary>
public class ServerPropertiesBoolTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("  true ", true)]
    [InlineData("false", false)]
    [InlineData("yes", false)]      // Boolean.parseBoolean: only "true" is true
    [InlineData("1", false)]
    [InlineData("", false)]
    public void OnlyTrueIsTrue(string raw, bool expected) =>
        Assert.Equal(expected, ServerPropertiesService.ParseBool(raw, fallback: !expected));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAbsentKeyIsTheFallback(bool fallback)
    {
        Assert.Equal(fallback, ServerPropertiesService.ParseBool(null, fallback));
        Assert.Equal(fallback,
            ServerPropertiesService.GetBool(new Dictionary<string, string>(), "white-list", fallback));
    }

    [Fact]
    public void AKeyIsFoundWhateverItsCase()
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["White-List"] = "true",
        };

        Assert.True(ServerPropertiesService.GetBool(props, "white-list", fallback: false));
    }
}
