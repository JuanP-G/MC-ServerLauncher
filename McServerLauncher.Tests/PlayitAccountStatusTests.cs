using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using static McServerLauncher.Services.PlayitAccountStatus;

namespace McServerLauncher.Tests;

/// <summary>
/// What the Playit account card says in each situation: connected or not, active or not, and through
/// which of the two ways.
/// </summary>
/// <remarks>
/// The card used to be one dot and some loose sentences, so someone with Playit installed but switched
/// off, or with a code Playit no longer accepted, saw a green dot and no reason to look further.
/// </remarks>
public class PlayitAccountStatusTests
{
    private static AccountLane App(AccountCard c) => c.Lanes[0];
    private static AccountLane Installed(AccountCard c) => c.Lanes[1];

    // --- the headline ---

    [Fact]
    public void WithNoWayInItSaysNotConnected()
    {
        var card = Build(new Input());

        Assert.Equal(Localizer.Get("Pa_Head_None"), card.Headline);
        Assert.Equal(AccountLevel.Idle, card.Level);
    }

    [Fact]
    public void ItAlwaysShowsBothWaysEvenWhenNeitherExists()
    {
        var card = Build(new Input());

        Assert.Equal(2, card.Lanes.Count);
        Assert.All(card.Lanes, l => Assert.True(l.Absent));
        Assert.NotEqual(App(card).Name, Installed(card).Name);
    }

    [Fact]
    public void ThePrimaryButtonIsConnectOnlyWhenNothingIsConnected()
    {
        var empty = App(Build(new Input()));
        var withInstalled = App(Build(new Input(HasInstalledKey: true)));

        Assert.Equal([LaneAction.Connect], empty.Actions);
        Assert.True(empty.Accent);
        Assert.Equal([LaneAction.Connect], withInstalled.Actions);
        Assert.False(withInstalled.Accent);
    }

    [Fact]
    public void BeforeAnyAnswerItSaysChecking()
    {
        var card = Build(new Input(HasAppCode: true, Agent: AgentRunState.Running, Checking: true));

        Assert.Equal(Localizer.Get("Pa_Head_Checking"), card.Headline);
        Assert.Equal(AccountLevel.Idle, card.Level);
    }

    [Fact]
    public void CheckingNeverOverridesNoWayIn() =>
        Assert.Equal(Localizer.Get("Pa_Head_None"), Build(new Input(Checking: true)).Headline);

    [Fact]
    public void TheAppsAgentRunningIsConnectedAndActive()
    {
        var card = Build(new Input(HasAppCode: true, Agent: AgentRunState.Running));

        Assert.Equal(Localizer.Get("Pa_Head_Active"), card.Headline);
        Assert.Equal(AccountLevel.Ok, card.Level);
        Assert.Equal(AccountLevel.Ok, App(card).Level);
        Assert.True(Installed(card).Absent);
    }

    [Fact]
    public void AnInstalledServiceRunningIsConnectedAndActive()
    {
        var card = Build(new Input(HasInstalledKey: true, ServiceInstalled: true,
            ServiceState: PlayitState.Running));

        Assert.Equal(Localizer.Get("Pa_Head_Active"), card.Headline);
        Assert.Equal(Localizer.Get("Pa_Inst_On"), Installed(card).Text);
        Assert.True(App(card).Absent);
    }

    [Fact]
    public void InstalledButSwitchedOffIsConnectedButNotActive()
    {
        // The case the green dot hid: the key is on disk, the account can be read, and nothing carries
        // traffic.
        var card = Build(new Input(HasInstalledKey: true, ServiceInstalled: true,
            ServiceState: PlayitState.Stopped));

        Assert.Equal(Localizer.Get("Pa_Head_Inactive"), card.Headline);
        Assert.Equal(AccountLevel.Warn, card.Level);
        Assert.Equal(Localizer.Get("Pa_Inst_Off"), Installed(card).Text);
        Assert.Equal([LaneAction.StartService], Installed(card).Actions);
    }

    [Fact]
    public void AKeyWithNoServiceOffersNothingToSwitch()
    {
        var lane = Installed(Build(new Input(HasInstalledKey: true, ServiceInstalled: false)));

        Assert.Equal(Localizer.Get("Pa_Inst_NoService"), lane.Text);
        Assert.Empty(lane.Actions);
    }

    [Fact]
    public void ARunningServiceOffersStop()
    {
        var lane = Installed(Build(new Input(HasInstalledKey: true, ServiceInstalled: true,
            ServiceState: PlayitState.Running)));

        Assert.Equal([LaneAction.StopService], lane.Actions);
    }

    [Theory]
    [InlineData(AgentRunState.Downloading)]
    [InlineData(AgentRunState.Starting)]
    public void TheAppsAgentComingUpIsConnectedAndStarting(AgentRunState state)
    {
        var card = Build(new Input(HasAppCode: true, Agent: state));

        Assert.Equal(Localizer.Get("Pa_Head_Starting"), card.Headline);
        Assert.Equal(AccountLevel.Warn, card.Level);
    }

    [Theory]
    [InlineData(AgentRunState.Stopped)]
    [InlineData(AgentRunState.Failed)]
    [InlineData(AgentRunState.Unsupported)]
    public void TheAppsAgentDownIsConnectedButNotActive(AgentRunState state)
    {
        var card = Build(new Input(HasAppCode: true, Agent: state));

        Assert.Equal(Localizer.Get("Pa_Head_Inactive"), card.Headline);
    }

    [Fact]
    public void AFailedAgentSaysWhy()
    {
        var lane = App(Build(new Input(HasAppCode: true, Agent: AgentRunState.Failed,
            AgentError: "no se encuentra el ejecutable")));

        Assert.Equal(AccountLevel.Bad, lane.Level);
        Assert.Contains("no se encuentra el ejecutable", lane.Text);
        Assert.Equal([LaneAction.Retry, LaneAction.ChangeCode, LaneAction.Disconnect], lane.Actions);
    }

    [Fact]
    public void ARunningAgentIsNotOfferedARetry() =>
        Assert.Equal([LaneAction.ChangeCode, LaneAction.Disconnect],
            App(Build(new Input(HasAppCode: true, Agent: AgentRunState.Running))).Actions);

    // --- a way Playit refuses ---

    [Fact]
    public void WhenEveryWayIsRefusedItSaysSoWhateverTheAgentIsDoing()
    {
        var card = Build(new Input(HasAppCode: true, Agent: AgentRunState.Running, AppRefused: true));

        Assert.Equal(Localizer.Get("Pa_Head_Refused"), card.Headline);
        Assert.Equal(AccountLevel.Bad, card.Level);
        Assert.Equal(Localizer.Get("Pa_App_Refused"), App(card).Text);
        Assert.Equal([LaneAction.ChangeCode, LaneAction.Disconnect], App(card).Actions);
    }

    [Fact]
    public void OneWayRefusedAndTheOtherActiveIsStillConnectedAndActive()
    {
        var card = Build(new Input(HasAppCode: true, Agent: AgentRunState.Running, AppRefused: true,
            HasInstalledKey: true, ServiceInstalled: true, ServiceState: PlayitState.Running));

        Assert.Equal(Localizer.Get("Pa_Head_Active"), card.Headline);
        Assert.Equal(AccountLevel.Bad, App(card).Level);
        Assert.Equal(AccountLevel.Ok, Installed(card).Level);
    }

    [Fact]
    public void ARefusedWayDoesNotCountAsCarryingTraffic()
    {
        // The refused code's agent may still be running, but the account does not answer it; the
        // installed service is off, so nothing is really active.
        var card = Build(new Input(HasAppCode: true, Agent: AgentRunState.Running, AppRefused: true,
            HasInstalledKey: true, ServiceInstalled: true, ServiceState: PlayitState.Stopped));

        Assert.Equal(Localizer.Get("Pa_Head_Inactive"), card.Headline);
    }

    [Fact]
    public void ARefusedInstalledKeyIsRed()
    {
        var card = Build(new Input(HasInstalledKey: true, InstalledRefused: true, ServiceInstalled: true,
            ServiceState: PlayitState.Running));

        Assert.Equal(Localizer.Get("Pa_Head_Refused"), card.Headline);
        Assert.Equal(Localizer.Get("Pa_Inst_Refused"), Installed(card).Text);
    }

    // --- the installed agent's key can only read ---

    [Fact]
    public void AReadOnlyKeyIsSaidOnTheInstalledLane()
    {
        var lane = Installed(Build(new Input(HasInstalledKey: true, ServiceInstalled: true,
            ServiceState: PlayitState.Running, InstalledReadOnly: true)));

        Assert.StartsWith(Localizer.Get("Pa_Inst_On"), lane.Text);
        Assert.EndsWith(Localizer.Get("Pa_Inst_ReadOnly"), lane.Text);
    }

    [Fact]
    public void ReadOnlyIsNotMentionedForARefusedKey()
    {
        var lane = Installed(Build(new Input(HasInstalledKey: true, InstalledRefused: true,
            InstalledReadOnly: true)));

        Assert.DoesNotContain(Localizer.Get("Pa_Inst_ReadOnly"), lane.Text);
    }

    // --- both ways, and an old key ---

    [Fact]
    public void BothWaysAreEachShownWithTheirOwnState()
    {
        var card = Build(new Input(HasAppCode: true, Agent: AgentRunState.Running, HasInstalledKey: true,
            ServiceInstalled: true, ServiceState: PlayitState.Stopped));

        Assert.Equal(Localizer.Get("Pa_Head_Active"), card.Headline);
        Assert.Equal(Localizer.Get("Pa_App_Running"), App(card).Text);
        Assert.Equal(Localizer.Get("Pa_Inst_Off"), Installed(card).Text);
    }

    [Fact]
    public void ASavedKeyFromAnOlderVersionGetsItsOwnLaneOnlyWhenItExists()
    {
        Assert.Equal(2, Build(new Input(HasAppCode: true)).Lanes.Count);

        var card = Build(new Input(HasSavedKey: true));

        Assert.Equal(3, card.Lanes.Count);
        Assert.Equal(Localizer.Get("Pa_Saved_State"), card.Lanes[2].Text);
        Assert.Equal([LaneAction.Disconnect], card.Lanes[2].Actions);
    }

    [Fact]
    public void DisconnectIsOfferedOnceNotOnEveryLane()
    {
        var card = Build(new Input(HasAppCode: true, HasSavedKey: true));

        Assert.Single(card.Lanes, l => l.Actions.Contains(LaneAction.Disconnect) && !l.Absent);
        Assert.Empty(card.Lanes[2].Actions);
    }

    [Fact]
    public void EveryTextTheCardCanSayIsTranslatedNotAKey()
    {
        // Localizer.Get returns the key itself when it is missing, so a typo ships as "Pa_Head_Actve".
        var inputs = new[]
        {
            new Input(),
            new Input(HasAppCode: true, Agent: AgentRunState.Running, Checking: true),
            new Input(HasAppCode: true, Agent: AgentRunState.Downloading),
            new Input(HasAppCode: true, Agent: AgentRunState.Starting, AppRefused: true),
            new Input(HasAppCode: true, Agent: AgentRunState.Failed, AgentError: "x"),
            new Input(HasAppCode: true, Agent: AgentRunState.Unsupported),
            new Input(HasAppCode: true, Agent: AgentRunState.Stopped, HasSavedKey: true, SavedRefused: true),
            new Input(HasInstalledKey: true, ServiceInstalled: true, ServiceState: PlayitState.Starting),
            new Input(HasInstalledKey: true, InstalledRefused: true, HasSavedKey: true),
            new Input(HasInstalledKey: true, InstalledReadOnly: true, HasSavedKey: true),
        };

        foreach (var card in inputs.Select(Build))
        {
            var texts = new List<string> { card.Headline };
            foreach (var lane in card.Lanes) texts.AddRange([lane.Name, lane.How, lane.Text]);
            Assert.All(texts, t => Assert.DoesNotMatch(@"^(Pa|Pk)_\w+$", t));
        }
    }
}
