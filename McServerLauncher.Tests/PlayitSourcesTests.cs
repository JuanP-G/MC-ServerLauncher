using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// Which ways into the Playit account the tunnels screen knows about, and when a rejected key counts as one.
/// </summary>
public class PlayitSourcesTests
{
    private static IReadOnlyList<PlayitConnection.Source> Of(AppSettings s, string? installed) =>
        PlayitConnection.Sources(s, () => installed);

    // --- where the keys come from ---

    [Fact]
    public void NothingAnywhereMeansNoWayIn() =>
        Assert.Empty(Of(new AppSettings(), installed: null));

    [Fact]
    public void AnInstalledAgentAloneIsAWayIn()
    {
        // The case that showed "not connected": nothing saved by the connect flow, Playit's own agent
        // installed with its key in playit.toml, and working tunnels.
        var sources = Of(new AppSettings(), installed: "installed-key");

        var only = Assert.Single(sources);
        Assert.Equal(PlayitConnection.SourceKind.InstalledAgent, only.Kind);
        Assert.Equal("installed-key", only.Key);
    }

    [Fact]
    public void TheAppsOwnAgentComesFirstThenTheInstalledOneThenASavedKey()
    {
        var settings = new AppSettings { PlayitAgentSecretKey = "app", PlayitApiKey = "saved" };

        var kinds = Of(settings, installed: "installed").Select(s => s.Kind);

        Assert.Equal(
            [PlayitConnection.SourceKind.AppAgent, PlayitConnection.SourceKind.InstalledAgent, PlayitConnection.SourceKind.SavedKey],
            kinds);
    }

    [Fact]
    public void TheSameKeyTwiceIsOneWayInNotTwo()
    {
        var settings = new AppSettings { PlayitAgentSecretKey = "same" };

        var only = Assert.Single(Of(settings, installed: "same"));

        Assert.Equal(PlayitConnection.SourceKind.AppAgent, only.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankKeysAreLeftOut(string? blank)
    {
        var settings = new AppSettings { PlayitAgentSecretKey = blank, PlayitApiKey = blank };

        Assert.Empty(Of(settings, installed: blank));
    }

    // --- a rejected key ---

    [Theory]
    [InlineData("Playit API: InvalidAgentKey")]
    [InlineData("Playit API: invalidagentkey")]
    [InlineData("InvalidApiKey")]
    [InlineData("Unauthorized")]
    [InlineData("AuthRequired")]
    public void AKeyProblemNamedInTheMessageIsAnAuthError(string message) =>
        Assert.True(new PlayitApiException(null, message).IsAuthError);

    [Theory]
    [InlineData("auth")]
    [InlineData("InvalidAgentKey")]
    public void AKeyProblemNamedInTheTypeIsAnAuthError(string type) =>
        Assert.True(new PlayitApiException(type, "Playit API: x").IsAuthError);

    [Theory]
    [InlineData("TunnelNotFound")]
    [InlineData("Playit API: tunnel limit reached")]
    public void OtherFailuresAreNotAuthErrors(string message)
    {
        // Treating these as key problems would send the same request three more times for nothing.
        Assert.False(new PlayitApiException("other", message).IsAuthError);
    }
}

/// <summary>
/// Playit's replies as they actually came back, and what the client makes of them.
/// </summary>
/// <remarks>
/// The two <c>auth</c> bodies were captured on 2026-10-03 from the real API with the key of an
/// installed agent, renaming and deleting a tunnel id that does not exist (so nothing on the account
/// changed). The <c>fail</c> body is the shape Playit's own client declares for an endpoint error
/// (<c>ApiResult::Fail</c> carrying <c>TunnelRenameError::TunnelNotFound</c>).
/// </remarks>
public class PlayitRepliesTests
{
    private const string ReadOnly = """{"status":"error","data":{"type":"auth","message":"NotAllowedWithReadOnly"}}""";
    private const string BadKey = """{"status":"error","data":{"type":"auth","message":"InvalidApiKey"}}""";
    private const string NotFound = """{"status":"fail","data":"TunnelNotFound"}""";
    private const string Ok = """{"status":"success","data":{"agent_id":"x","tunnels":[]}}""";

    [Fact]
    public void ASuccessIsNoError() => Assert.Null(PlayitApiService.ErrorFrom(Ok));

    [Fact]
    public void AReadOnlyKeyIsAnAuthErrorThatSaysSo()
    {
        var e = PlayitApiService.ErrorFrom(ReadOnly)!;

        Assert.True(e.IsAuthError);
        Assert.True(e.IsReadOnlyRefusal);
    }

    [Fact]
    public void ARejectedKeyIsNotReadOnly()
    {
        var e = PlayitApiService.ErrorFrom(BadKey)!;

        Assert.True(e.IsAuthError);
        Assert.False(e.IsReadOnlyRefusal);
    }

    [Fact]
    public void AFailWithABareStringIsReadInsteadOfCrashing()
    {
        // This shape used to throw "requires an element of type Object" from inside the parser.
        var e = PlayitApiService.ErrorFrom(NotFound)!;

        Assert.Equal("fail", e.ErrorType);
        Assert.Contains("TunnelNotFound", e.Message);
        Assert.False(e.IsAuthError);
    }
}
