using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The Windows Firewall notice on the server's page: when it shows, what it says, and that
/// dismissing it holds for that problem only.
/// </summary>
/// <remarks>
/// Run against a fake firewall that is shaped like the issue's machine (java.exe allowed on the
/// private network, a public VPN). What Windows itself returns is checked by hand: the service
/// reads it through COM, which a test cannot fake.
/// </remarks>
[Collection("avalonia")]
public class FirewallNoticeTests(AvaloniaFixture ui) : IDisposable
{
    private const int Port = 19132, Tcp = 6, Udp = 17, Any = 256, All = 0x7FFFFFFF, Private = 2, Public = 4;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-fwn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    private string JavaExe()
    {
        Directory.CreateDirectory(_folder);
        var exe = Path.Combine(_folder, "java.exe");
        File.WriteAllText(exe, "");
        return exe;
    }

    private static FirewallRule JavaRule(string exe, int protocol, int profiles) =>
        new("Java", protocol, null, profiles, true, true, true, exe, null);

    private ServerViewModel Server(FakeFirewall firewall, bool crossplay = true)
    {
        var vm = new ServerViewModel(new ServerConfig
        {
            Name = "ServerMC",
            FolderPath = _folder,
            Type = ServerType.Paper,
            CrossplayEnabled = crossplay,
            BedrockPort = Port,
            JavaPath = JavaExe()
        });
        vm.Firewall = new WindowsFirewallService(firewall);
        return vm;
    }

    /// <summary>The check is async and comes back to the UI thread: pump until it has.</summary>
    private static void Settle(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            AvaloniaFixture.Pump();
            Thread.Sleep(10);
        }
        AvaloniaFixture.Pump();
        task.GetAwaiter().GetResult();
    }

    /// <summary>The issue's machine: java.exe allowed on the private network, and a public VPN.</summary>
    private FakeFirewall IssueMachine() => new(
        allow: [JavaRule(JavaExe(), Tcp, Private), JavaRule(JavaExe(), Udp, Private)],
        networks: [new FirewallNetwork("Radmin VPN", Public)]);

    [Fact]
    public void ShowsTheNoticeNamingTheNetworkAndThePort()
    {
        if (!WindowsFirewallService.IsSupported) return;
        ui.Run(() =>
        {
            var vm = Server(IssueMachine());
            Assert.False(vm.ShowFirewallNotice);   // nothing is known until it has looked

            Settle(vm.RefreshFirewallNoticeAsync());

            Assert.True(vm.ShowFirewallNotice);
            Assert.Contains("Radmin VPN", vm.FirewallNoticeText);
            Assert.Contains("19132", vm.FirewallNoticeText);
        });
    }

    [Fact]
    public void StaysAwayWhenNothingIsWrong()
    {
        if (!WindowsFirewallService.IsSupported) return;
        ui.Run(() =>
        {
            var exe = JavaExe();
            var fake = new FakeFirewall(
                allow: [JavaRule(exe, Tcp, All), JavaRule(exe, Udp, All)],
                networks: [new FirewallNetwork("Radmin VPN", Public)]);
            var vm = Server(fake);

            Settle(vm.RefreshFirewallNoticeAsync());

            Assert.False(vm.ShowFirewallNotice);
        });
    }

    [Fact]
    public void StaysAwayWithoutCrossplayAndDoesNotReadTheFirewall()
    {
        if (!WindowsFirewallService.IsSupported) return;
        ui.Run(() =>
        {
            var fake = IssueMachine();
            var vm = Server(fake, crossplay: false);

            Settle(vm.RefreshFirewallNoticeAsync());

            Assert.False(vm.ShowFirewallNotice);
            Assert.Equal(0, fake.Reads);
        });
    }

    [Fact]
    public void DismissingHoldsForThatProblemOnly()
    {
        if (!WindowsFirewallService.IsSupported) return;
        ui.Run(() =>
        {
            var fake = IssueMachine();
            var vm = Server(fake);
            var saved = 0;
            vm.ConfigChanged += () => saved++;
            Settle(vm.RefreshFirewallNoticeAsync());

            vm.DismissFirewallNoticeCommand.Execute(null);

            Assert.False(vm.ShowFirewallNotice);
            Assert.Equal(1, saved);   // remembered, so it is still gone after a restart

            // The same problem on the next look: still quiet.
            Settle(vm.RefreshFirewallNoticeAsync());
            Assert.False(vm.ShowFirewallNotice);

            // Another network turns up with the same problem: that is a different problem.
            fake.Networks = [new FirewallNetwork("Radmin VPN", Public), new FirewallNetwork("Hamachi", Public)];
            Settle(vm.RefreshFirewallNoticeAsync());
            Assert.True(vm.ShowFirewallNotice);
            Assert.Contains("Hamachi", vm.FirewallNoticeText);
        });
    }

    [Fact]
    public void ABlockRuleAsksForTheConsoleNotForAnotherPermission()
    {
        if (!WindowsFirewallService.IsSupported) return;
        ui.Run(() =>
        {
            var exe = JavaExe();
            var fake = new FakeFirewall(
                allow: [],
                networks: [new FirewallNetwork("Radmin VPN", Public)],
                block: [new FirewallRule("java.exe", Any, null, Public, true, false, true, exe, null)]);
            var vm = Server(fake);

            Settle(vm.RefreshFirewallNoticeAsync());

            Assert.True(vm.ShowFirewallNotice);
            Assert.Contains("java.exe", vm.FirewallNoticeText);
            Assert.Equal(Localization.Localizer.Get("Firewall_OpenConsole"), vm.FirewallFixLabel);
        });
    }

    [Fact]
    public void AllowingTheFixAndLookingAgainClearsTheNotice()
    {
        if (!WindowsFirewallService.IsSupported) return;
        ui.Run(() =>
        {
            var fake = IssueMachine();
            fake.OnRun = f => f.Allow.AddRange([
                new FirewallRule(WindowsFirewallService.RuleName(Port, "TCP"), Tcp, "19132", All, true, true, true, null, null),
                new FirewallRule(WindowsFirewallService.RuleName(Port, "UDP"), Udp, "19132", All, true, true, true, null, null)]);
            var vm = Server(fake);
            Settle(vm.RefreshFirewallNoticeAsync());
            Assert.True(vm.ShowFirewallNotice);

            Settle(vm.FixFirewallCommand.ExecuteAsync(null));

            Assert.Single(fake.Ran);
            Assert.Contains("localport=19132", fake.Ran[0]);
            Assert.False(vm.ShowFirewallNotice);
            Assert.False(vm.FirewallBusy);
        });
    }

    [Fact]
    public void DecliningThePromptKeepsTheNoticeAndSaysNothingChanged()
    {
        if (!WindowsFirewallService.IsSupported) return;
        ui.Run(() =>
        {
            var fake = IssueMachine();
            fake.Declines = true;
            var vm = Server(fake);
            Settle(vm.RefreshFirewallNoticeAsync());

            Settle(vm.FixFirewallCommand.ExecuteAsync(null));

            Assert.True(vm.ShowFirewallNotice);
            Assert.Contains(Localization.Localizer.Get("Firewall_Cancelled"), vm.FirewallNoticeText);
        });
    }

    [Fact]
    public void AServerThatRunsButDoesNotListenYetIsNotBlamedOnTheFirewall()
    {
        if (!WindowsFirewallService.IsSupported) return;
        ui.Run(() =>
        {
            var fake = IssueMachine();
            var vm = Server(fake);
            vm.State = ServerState.Running;
            vm.Config.BedrockPort = 61998;   // nobody listens here

            Settle(vm.RefreshFirewallNoticeAsync());

            Assert.False(vm.ShowFirewallNotice);
            Assert.Equal(0, fake.Reads);
        });
    }

    /// <summary>A firewall made of lists, with the commands the notice runs recorded.</summary>
    private sealed class FakeFirewall(
        FirewallRule[] allow, FirewallNetwork[] networks, FirewallRule[]? block = null) : IFirewallBackend
    {
        public List<FirewallRule> Allow { get; } = [.. allow];
        public FirewallNetwork[] Networks { get; set; } = networks;
        public List<string> Ran { get; } = [];
        public int Reads { get; private set; }
        public bool Declines { get; set; }
        public Action<FakeFirewall>? OnRun { get; set; }

        public IReadOnlyList<FirewallRule> ReadRules() => Allow.ToList();

        public FirewallInputs ReadInputs()
        {
            Reads++;
            return new FirewallInputs(Allow.ToList(), block ?? [],
                [new FirewallProfile(1, true, true), new FirewallProfile(2, true, true), new FirewallProfile(4, true, true)],
                Networks);
        }

        public int RunElevated(string arguments)
        {
            if (Declines) throw new OperationCanceledException();
            Ran.Add(arguments);
            OnRun?.Invoke(this);
            return 0;
        }
    }
}
