using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What was saved into server.properties while the server ran is written again after the server
/// overwrites the file.
/// </summary>
/// <remarks>
/// A running server rewrites the whole file from what it loaded at start whenever its whitelist is
/// switched, so a MOTD or a player limit saved from the settings page meanwhile was undone by the
/// switch on the Players tab.
/// </remarks>
public class PropertiesSavedWhileRunningTests
{
    [Fact]
    public void WhatWasSavedIsOwedBackAndTheLatestValueWins()
    {
        var saved = new PropertiesSavedWhileRunning();
        saved.Note(new Dictionary<string, string> { ["motd"] = "first", ["max-players"] = "30" });
        saved.Note(new Dictionary<string, string> { ["MOTD"] = "second" });

        var owed = saved.ToRestore();

        Assert.Equal("second", owed["motd"]);
        Assert.Equal("30", owed["max-players"]);
        Assert.Equal(2, owed.Count);
    }

    [Fact]
    public void TheWhitelistItselfIsNeverPutBack()
    {
        // It is what the command just set; an older saved value would undo the switch.
        var saved = new PropertiesSavedWhileRunning();
        saved.Note(new Dictionary<string, string> { ["white-list"] = "true", ["pvp"] = "false" });

        Assert.False(saved.ToRestore().ContainsKey("white-list"));
        Assert.Equal("false", saved.ToRestore()["pvp"]);
    }

    [Fact]
    public void NothingIsOwedOnceTheServerStops()
    {
        var saved = new PropertiesSavedWhileRunning();
        saved.Note(new Dictionary<string, string> { ["motd"] = "x" });

        saved.Clear();

        Assert.Empty(saved.ToRestore());
    }
}
