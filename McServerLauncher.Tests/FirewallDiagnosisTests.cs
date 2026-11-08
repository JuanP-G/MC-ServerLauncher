using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// When the launcher tells someone their Windows Firewall is in the way of Bedrock players.
/// </summary>
/// <remarks>
/// The launcher cannot see a dropped packet, so the notice is a deduction from the rules and the
/// networks. A wrong deduction either nags someone who has no problem or stays quiet for someone who
/// does, so every case that decides it is pinned here against rules shaped like the ones Windows
/// writes (issue 108: java.exe allowed on the private profile only, and a VPN that is public).
/// </remarks>
public class FirewallDiagnosisTests
{
    private const int Port = 19132;
    private const int Tcp = 6, Udp = 17, Any = 256, All = 0x7FFFFFFF;
    private const int Domain = 1, Private = 2, Public = 4;
    private const string Java = @"C:\Java\jdk-21\bin\java.exe";
    private const string Launcher = @"C:\Apps\MC Server Launcher\McServerLauncher.exe";

    private static readonly FirewallOwner JavaOwner = new(Java, NeedsUdp: true);

    private static FirewallRule Rule(string name = "rule", int protocol = Any, string? ports = null,
        int profiles = All, string? app = null, bool narrowed = false) =>
        new(name, protocol, ports, profiles, Inbound: true, Allow: true, Enabled: true, app, null, narrowed);

    /// <summary>The two rules Windows makes when "allow java.exe?" is answered, for the profiles ticked.</summary>
    private static FirewallRule[] JavaPrompt(int profiles, string app = Java) =>
        [Rule("Java", Tcp, app: app, profiles: profiles), Rule("Java", Udp, app: app, profiles: profiles)];

    private static FirewallRule Block(string name, int profiles, string? app = Java, int protocol = Any,
        string? ports = null) =>
        new(name, protocol, ports, profiles, Inbound: true, Allow: false, Enabled: true, app, null);

    private static FirewallProfile[] EveryProfileBlocks() =>
        [new(Domain, true, true), new(Private, true, true), new(Public, true, true)];

    private static FirewallFinding Run(FirewallRule[] allow, FirewallNetwork[] networks,
        FirewallRule[]? block = null, FirewallProfile[]? profiles = null, params FirewallOwner[] owners) =>
        FirewallDiagnosis.Diagnose(Port, owners.Length == 0 ? [JavaOwner] : owners,
            new FirewallInputs(allow, block ?? [], profiles ?? EveryProfileBlocks(), networks));

    // --- the case of the issue ---

    [Fact]
    public void JavaAllowedOnThePrivateNetworkOnlyIsAProblemOnAPublicVpn()
    {
        var finding = Run(JavaPrompt(Private), [new("Radmin VPN", Public)]);

        Assert.Equal(FirewallCause.NoRule, finding.Cause);
        Assert.Equal(["Radmin VPN"], finding.Networks);
    }

    [Fact]
    public void TheSameRuleIsFineWhenTheNetworkIsPrivate() =>
        Assert.Equal(FirewallCause.None, Run(JavaPrompt(Private), [new("Home", Private)]).Cause);

    [Fact]
    public void OnlyTheNetworksThatFailAreNamed()
    {
        var finding = Run(JavaPrompt(Private), [new("Home", Private), new("Radmin VPN", Public), new("Corp", Domain)]);

        Assert.Equal(["Radmin VPN", "Corp"], finding.Networks);
    }

    // --- what lets the port in ---

    [Fact]
    public void ARuleForThePortOnEveryNetworkSettlesIt() =>
        Assert.Equal(FirewallCause.None, Run([Rule("mine", Tcp, "19132"), Rule("mine", Udp, "19132")], [new("VPN", Public)]).Cause);

    [Fact]
    public void JavaAllowedOnThePublicNetworkSettlesIt() =>
        Assert.Equal(FirewallCause.None, Run(JavaPrompt(Public | Private), [new("VPN", Public)]).Cause);

    [Fact]
    public void JavaNeedsUdpToo()
    {
        // Only TCP for java.exe: the handshake gets in and the game itself does not.
        var finding = Run([Rule("Java", Tcp, app: Java)], [new("VPN", Public)]);

        Assert.Equal(FirewallCause.NoRule, finding.Cause);
    }

    [Fact]
    public void ThePortAloneIsNotEnoughWhenItIsAnotherPort() =>
        Assert.Equal(FirewallCause.NoRule, Run([Rule("mine", Any, "25565")], [new("VPN", Public)]).Cause);

    [Fact]
    public void ARuleForAnotherProgramDoesNotHelp() =>
        Assert.Equal(FirewallCause.NoRule,
            Run(JavaPrompt(All, app: @"C:\Other\java.exe"), [new("VPN", Public)]).Cause);

    [Fact]
    public void AProgramPathIsComparedWithoutRegardToCase() =>
        Assert.Equal(FirewallCause.None,
            Run(JavaPrompt(All, app: Java.ToLowerInvariant()), [new("VPN", Public)]).Cause);

    [Fact]
    public void ARuleLimitedToAStoreAppOrAUserDoesNotHelp() =>
        Assert.Equal(FirewallCause.NoRule,
            Run([Rule("store app", Any, narrowed: true)], [new("VPN", Public)]).Cause);

    // --- the sleeping server's listener ---

    [Fact]
    public void TheLauncherListeningWhileTheServerSleepsNeedsTcpOnly()
    {
        var owners = new[] { new FirewallOwner(Launcher, NeedsUdp: false) };

        Assert.Equal(FirewallCause.None,
            Run([Rule("launcher", Tcp, app: Launcher)], [new("VPN", Public)], owners: owners).Cause);
        // And a rule for Java is no help to it: Java is not the one listening then.
        Assert.Equal(FirewallCause.NoRule,
            Run(JavaPrompt(All), [new("VPN", Public)], owners: owners).Cause);
    }

    // --- a rule that blocks ---

    [Fact]
    public void ABlockRuleWinsOverAnAllowRule()
    {
        // What Windows writes when the "allow?" prompt is turned down: per program, public profile.
        var finding = Run([Rule("mine", Tcp, "19132"), Rule("mine", Udp, "19132")], [new("VPN", Public)],
            block: [Block("java.exe", Public)]);

        Assert.Equal(FirewallCause.Blocked, finding.Cause);
        Assert.Equal(["java.exe"], finding.BlockingRules);
        Assert.Equal(["VPN"], finding.Networks);
    }

    [Fact]
    public void ABlockRuleOnAnotherProfileIsHarmless() =>
        Assert.Equal(FirewallCause.None,
            Run(JavaPrompt(All), [new("VPN", Public)], block: [Block("java.exe", Private)]).Cause);

    [Fact]
    public void ABlockRuleForAnotherProgramIsHarmless() =>
        Assert.Equal(FirewallCause.None,
            Run(JavaPrompt(All), [new("VPN", Public)], block: [Block("other", Public, app: @"C:\Other\x.exe")]).Cause);

    [Fact]
    public void ABlockRuleForAnotherPortIsHarmless() =>
        Assert.Equal(FirewallCause.None,
            Run(JavaPrompt(All), [new("VPN", Public)], block: [Block("ssh", Public, app: null, ports: "22")]).Cause);

    [Fact]
    public void APortBlockWithNoProgramBlocksToo()
    {
        var finding = Run(JavaPrompt(All), [new("VPN", Public)], block: [Block("mine", Public, app: null, ports: "19000-19200")]);

        Assert.Equal(FirewallCause.Blocked, finding.Cause);
    }

    // --- when the firewall is not in the way ---

    [Fact]
    public void AProfileWithTheFirewallOffTurnsNobodyAway()
    {
        FirewallProfile[] profiles = [new(Domain, true, true), new(Private, true, true), new(Public, Enabled: false, BlocksInbound: true)];

        Assert.Equal(FirewallCause.None, Run([], [new("VPN", Public)], profiles: profiles).Cause);
    }

    [Fact]
    public void AProfileThatLetsEverythingInTurnsNobodyAway()
    {
        FirewallProfile[] profiles = [new(Public, Enabled: true, BlocksInbound: false)];

        Assert.Equal(FirewallCause.None, Run([], [new("VPN", Public)], profiles: profiles).Cause);
    }

    [Fact]
    public void NoNetworkMeansNothingToWarnAbout() =>
        Assert.Equal(FirewallCause.None, Run([], []).Cause);

    [Fact]
    public void NothingToCheckWhenNobodyListens() =>
        Assert.Equal(FirewallCause.None,
            FirewallDiagnosis.Diagnose(Port, [], new FirewallInputs([], [], EveryProfileBlocks(), [new("VPN", Public)])).Cause);

    // --- the dismissal ---

    [Fact]
    public void TheSignatureNamesTheProblemNotTheOrder()
    {
        var a = new FirewallFinding(FirewallCause.NoRule, ["B", "A"], []);
        var b = new FirewallFinding(FirewallCause.NoRule, ["A", "B"], []);

        Assert.Equal(a.Signature, b.Signature);
        Assert.NotEqual(a.Signature, new FirewallFinding(FirewallCause.Blocked, ["A", "B"], ["x"]).Signature);
        Assert.NotEqual(a.Signature, new FirewallFinding(FirewallCause.NoRule, ["A"], []).Signature);
        Assert.Equal("", FirewallFinding.Nothing.Signature);
    }

    // --- reading Windows ---

    [Theory]
    [InlineData(0, Public)]
    [InlineData(1, Private)]
    [InlineData(2, Domain)]
    public void TheNetworkCategoryBecomesAProfile(int category, int profile) =>
        Assert.Equal(profile, FirewallDiagnosis.ProfileOf(category));

    [Fact]
    public void AProgramIsFoundByPathOrThroughThePath()
    {
        var dir = Directory.CreateTempSubdirectory("fwdiag").FullName;
        try
        {
            var exe = Path.Combine(dir, "java.exe");
            File.WriteAllText(exe, "");

            Assert.Equal(exe, FirewallDiagnosis.Resolve(exe));
            Assert.Equal(exe, FirewallDiagnosis.Resolve("java", dir + Path.PathSeparator + @"C:\nowhere"));
            Assert.Null(FirewallDiagnosis.Resolve("java", @"C:\nowhere"));
            Assert.Null(FirewallDiagnosis.Resolve(Path.Combine(dir, "missing.exe")));
            Assert.Null(FirewallDiagnosis.Resolve(""));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AFirewallThatCannotBeReadDiagnosesNothing()
    {
        if (!WindowsFirewallService.IsSupported) return;
        var service = new WindowsFirewallService(new Unreadable());

        Assert.Equal(FirewallCause.None, service.Diagnose(Port, [JavaOwner]).Cause);
    }

    private sealed class Unreadable : IFirewallBackend
    {
        public IReadOnlyList<FirewallRule> ReadRules() => throw new InvalidOperationException();
        public FirewallInputs ReadInputs() => throw new InvalidOperationException();
        public int RunElevated(string arguments) => throw new InvalidOperationException();
    }
}
