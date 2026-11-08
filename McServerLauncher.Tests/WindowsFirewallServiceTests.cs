using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The Windows Firewall button: what counts as "already allowed", and the command it runs as
/// administrator.
/// </summary>
/// <remarks>
/// Changing a firewall is not something to find out about by pressing the button. The decision of
/// whether a rule helps and the text of the elevated command are pinned here; the one thing these
/// cannot show, Windows actually accepting the command, is checked once by hand.
/// </remarks>
public class WindowsFirewallServiceTests
{
    private const int Port = 19132;
    private const int Tcp = 6, Udp = 17, Any = 256, All = 0x7FFFFFFF;

    private static FirewallRule Rule(string name = "x", int protocol = Tcp, string? ports = "19132",
        int profiles = All, bool inbound = true, bool allow = true, bool enabled = true,
        string? app = null, string? service = null, bool narrowed = false) =>
        new(name, protocol, ports, profiles, inbound, allow, enabled, app, service, narrowed);

    [Fact]
    public void ARuleLimitedToSomethingElseDoesNotCover()
    {
        // The rules Windows keeps for Store apps name no program, no ports and every network, so
        // on a real machine they made every port look open until they were told apart.
        Assert.False(WindowsFirewallService.Covers(Rule(protocol: Any, ports: null, narrowed: true), Port, Tcp));
    }

    // --- does a rule help ---

    [Fact]
    public void APlainPortRuleCoversItsPort() =>
        Assert.True(WindowsFirewallService.Covers(Rule(), Port, Tcp));

    [Theory]
    [InlineData(Tcp, Udp)]
    [InlineData(Udp, Tcp)]
    public void ARuleOnlyCoversItsOwnProtocol(int ruleProtocol, int asked) =>
        Assert.False(WindowsFirewallService.Covers(Rule(protocol: ruleProtocol), Port, asked));

    [Fact]
    public void ARuleForAnyProtocolCoversBoth()
    {
        Assert.True(WindowsFirewallService.Covers(Rule(protocol: Any), Port, Tcp));
        Assert.True(WindowsFirewallService.Covers(Rule(protocol: Any), Port, Udp));
    }

    [Fact]
    public void ARuleThatSkipsThePublicNetworkDoesNotCover()
    {
        // The case behind the button: the rule Windows makes for java.exe has the private profile
        // only, and a VPN is usually public.
        Assert.False(WindowsFirewallService.Covers(Rule(profiles: 1 | 2), Port, Tcp));
        Assert.True(WindowsFirewallService.Covers(Rule(profiles: 1 | 2 | 4), Port, Tcp));
    }

    [Theory]
    [InlineData(false, true, true, null, null)]       // outbound
    [InlineData(true, false, true, null, null)]       // a block rule
    [InlineData(true, true, false, null, null)]       // switched off
    [InlineData(true, true, true, "C:\\java\\bin\\java.exe", null)]   // one program only
    [InlineData(true, true, true, null, "Spooler")]   // one service only
    public void ARuleThatIsNotAnOpenInboundAllowDoesNotCover(
        bool inbound, bool allow, bool enabled, string? app, string? service) =>
        Assert.False(WindowsFirewallService.Covers(
            Rule(inbound: inbound, allow: allow, enabled: enabled, app: app, service: service), Port, Tcp));

    [Theory]
    [InlineData("19132", true)]
    [InlineData("*", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("19000-19200", true)]
    [InlineData("80,443,19132", true)]
    [InlineData("80, 19100-19140", true)]
    [InlineData("19133", false)]
    [InlineData("1-19131", false)]
    [InlineData("19133-20000", false)]
    [InlineData("RPC", false)]
    [InlineData("80,RPC", false)]
    [InlineData("junk-", false)]
    public void ReadsAPortList(string? list, bool expected) =>
        Assert.Equal(expected, WindowsFirewallService.PortListContains(list, Port));

    // --- what the page is told ---

    [Fact]
    public void WithNoRulesThePortIsClosed()
    {
        if (!WindowsFirewallService.IsSupported) return;
        Assert.Equal(FirewallState.Closed, new WindowsFirewallService(new FakeBackend()).GetState(Port));
    }

    [Fact]
    public void OneProtocolAloneIsStillClosed()
    {
        if (!WindowsFirewallService.IsSupported) return;
        var backend = new FakeBackend(Rule(WindowsFirewallService.RuleName(Port, "TCP")));
        Assert.Equal(FirewallState.Closed, new WindowsFirewallService(backend).GetState(Port));
    }

    [Fact]
    public void TheLaunchersRulesMeanOpen()
    {
        if (!WindowsFirewallService.IsSupported) return;
        var backend = new FakeBackend(
            Rule(WindowsFirewallService.RuleName(Port, "TCP")),
            Rule(WindowsFirewallService.RuleName(Port, "UDP"), Udp));
        Assert.Equal(FirewallState.Open, new WindowsFirewallService(backend).GetState(Port));
    }

    [Fact]
    public void SomeoneElsesRulesMeanThereIsNothingToCreate()
    {
        if (!WindowsFirewallService.IsSupported) return;
        var backend = new FakeBackend(Rule("my own", Any, "19000-19200"));
        Assert.Equal(FirewallState.OpenElsewhere, new WindowsFirewallService(backend).GetState(Port));
    }

    [Fact]
    public void AFirewallThatCannotBeReadShowsNothing()
    {
        if (!WindowsFirewallService.IsSupported) return;
        var backend = new FakeBackend { ReadFails = true };
        Assert.Equal(FirewallState.Unavailable, new WindowsFirewallService(backend).GetState(Port));
    }

    // --- the command ---

    [Fact]
    public void TheAllowCommandReplacesBothRulesForThePort()
    {
        var args = WindowsFirewallService.AllowArguments(Port);

        Assert.StartsWith("/c ", args);
        // Deleted first so pressing twice cannot leave two rules; both protocols, every network.
        Assert.True(args.IndexOf("delete rule", StringComparison.Ordinal)
                    < args.IndexOf("add rule", StringComparison.Ordinal));
        Assert.Contains("protocol=tcp localport=19132 profile=any", args);
        Assert.Contains("protocol=udp localport=19132 profile=any", args);
        Assert.Equal(2, args.Split("add rule").Length - 1);
        Assert.Equal(2, args.Split("dir=in action=allow").Length - 1);
        Assert.Contains("MC Server Launcher - Bedrock 19132 (TCP)", args);
        Assert.Contains("MC Server Launcher - Bedrock 19132 (UDP)", args);
    }

    [Fact]
    public void TheRemoveCommandOnlyDeletesAndOnlyThatPort()
    {
        var args = WindowsFirewallService.RemoveArguments(25570);

        Assert.DoesNotContain("add rule", args);
        Assert.Equal(2, args.Split("delete rule").Length - 1);
        Assert.Contains("Bedrock 25570 (TCP)", args);
        Assert.DoesNotContain("19132", args);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void APortOutOfRangeNeverReachesTheCommandLine(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsFirewallService.AllowArguments(port));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsFirewallService.RemoveArguments(port));
    }

    // --- asking Windows ---

    [Fact]
    public async Task AllowingRunsTheCommandAndChecksTheResult()
    {
        var backend = new FakeBackend { OnRun = b => b.Add(
            Rule(WindowsFirewallService.RuleName(Port, "TCP")),
            Rule(WindowsFirewallService.RuleName(Port, "UDP"), Udp)) };

        var outcome = await new WindowsFirewallService(backend).AllowAsync(Port);

        Assert.Equal(FirewallOutcome.Done, outcome);
        Assert.Equal(WindowsFirewallService.AllowArguments(Port), Assert.Single(backend.Ran));
    }

    [Fact]
    public async Task DecliningThePromptChangesNothingAndSaysSo()
    {
        var backend = new FakeBackend { Declines = true };

        var outcome = await new WindowsFirewallService(backend).AllowAsync(Port);

        Assert.Equal(FirewallOutcome.Cancelled, outcome);
    }

    [Fact]
    public async Task ACommandThatLeavesNoRuleBehindFailsEvenWithExitCodeZero()
    {
        // cmd reports the last command of a chain; the firewall is what says whether it worked.
        var backend = new FakeBackend { ExitCode = 0 };

        var outcome = await new WindowsFirewallService(backend).AllowAsync(Port);

        Assert.Equal(FirewallOutcome.Failed, outcome);
    }

    [Fact]
    public async Task ACommandThatCannotStartFails()
    {
        var backend = new FakeBackend { Throws = true };

        Assert.Equal(FirewallOutcome.Failed, await new WindowsFirewallService(backend).AllowAsync(Port));
    }

    [Fact]
    public async Task RemovingIsDoneWhenNoOwnRuleIsLeft()
    {
        var backend = new FakeBackend(
            Rule(WindowsFirewallService.RuleName(Port, "TCP")),
            Rule(WindowsFirewallService.RuleName(Port, "UDP"), Udp))
        { OnRun = b => b.Clear() };

        var outcome = await new WindowsFirewallService(backend).RemoveAsync(Port);

        Assert.Equal(FirewallOutcome.Done, outcome);
        Assert.Equal(WindowsFirewallService.RemoveArguments(Port), Assert.Single(backend.Ran));
    }

    [Fact]
    public async Task RemovingFailsWhenARuleSurvives()
    {
        var backend = new FakeBackend(Rule(WindowsFirewallService.RuleName(Port, "TCP")));

        Assert.Equal(FirewallOutcome.Failed, await new WindowsFirewallService(backend).RemoveAsync(Port));
    }

    private sealed class FakeBackend : IFirewallBackend
    {
        private readonly List<FirewallRule> _rules;

        public FakeBackend(params FirewallRule[] rules) => _rules = [.. rules];

        public List<string> Ran { get; } = [];
        public bool ReadFails { get; init; }
        public bool Declines { get; init; }
        public bool Throws { get; init; }
        public int ExitCode { get; init; }

        /// <summary>What running the command does to the rules, as Windows would.</summary>
        public Action<FakeRules>? OnRun { get; init; }

        public IReadOnlyList<FirewallRule> ReadRules() =>
            ReadFails ? throw new InvalidOperationException("no firewall") : _rules.ToList();

        public int RunElevated(string arguments)
        {
            if (Declines) throw new OperationCanceledException();
            if (Throws) throw new InvalidOperationException("cannot start");
            Ran.Add(arguments);
            OnRun?.Invoke(new FakeRules(_rules));
            return ExitCode;
        }
    }

    private sealed class FakeRules(List<FirewallRule> rules)
    {
        public void Add(params FirewallRule[] added) => rules.AddRange(added);
        public void Clear() => rules.Clear();
    }
}
