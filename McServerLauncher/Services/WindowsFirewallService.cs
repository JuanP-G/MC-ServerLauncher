using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace McServerLauncher.Services;

/// <summary>One inbound-or-outbound rule of Windows Defender Firewall, reduced to what matters here.</summary>
/// <param name="Name">The rule's display name.</param>
/// <param name="Protocol">The IP protocol number: 6 is TCP, 17 is UDP, 256 is "any".</param>
/// <param name="LocalPorts">The rule's list as Windows writes it ("19132", "1000-2000,3000"), or null for any.</param>
/// <param name="Profiles">The network profiles as flags: 1 domain, 2 private, 4 public; all of them is 0x7FFFFFFF.</param>
/// <param name="Inbound">Whether the rule is about incoming traffic.</param>
/// <param name="Allow">Whether it allows (true) or blocks (false).</param>
/// <param name="Enabled">Whether the rule is switched on.</param>
/// <param name="Application">The one program it is limited to, or null for any.</param>
/// <param name="Service">The one Windows service it is limited to, or null for any.</param>
/// <param name="Narrowed">
/// Whether it is limited in some other way: to a Store app, a user or machine, to some remote
/// addresses or ports, or to one kind of network adapter. Such a rule lets the port in for somebody
/// else's traffic, not for a player on a VPN.
/// </param>
internal sealed record FirewallRule(
    string Name, int Protocol, string? LocalPorts, int Profiles,
    bool Inbound, bool Allow, bool Enabled, string? Application, string? Service, bool Narrowed = false);

/// <summary>What the firewall says about one port.</summary>
public enum FirewallState
{
    /// <summary>Not Windows, or the firewall service cannot be read: the page shows nothing.</summary>
    Unavailable,
    /// <summary>No rule lets the port in, over both TCP and UDP, on every kind of network.</summary>
    Closed,
    /// <summary>The launcher's own rules are in place.</summary>
    Open,
    /// <summary>Somebody else's rule already covers it, so there is nothing to create.</summary>
    OpenElsewhere
}

/// <summary>How asking Windows to change the firewall ended.</summary>
public enum FirewallOutcome { Done, Cancelled, Failed }

/// <summary>Where the firewall's rules come from and how a change gets made. Faked in tests.</summary>
internal interface IFirewallBackend
{
    /// <summary>Every rule, read without administrator rights. Throws when the firewall cannot be read.</summary>
    IReadOnlyList<FirewallRule> ReadRules();

    /// <summary>
    /// Runs <c>cmd.exe</c> with <paramref name="arguments"/> as administrator and returns its exit code.
    /// Throws <see cref="OperationCanceledException"/> when the user turns the prompt down.
    /// </summary>
    int RunElevated(string arguments);
}

/// <summary>
/// Lets a server's Bedrock port through Windows Firewall, with one click instead of a trip to a
/// control panel.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists: Windows asks "allow java.exe?" the first time a server listens, and that rule
/// covers the network profiles ticked at that moment — by default only the private one. A VPN such
/// as Radmin or Hamachi is usually classed as public, so Java players get in while Bedrock players,
/// whose NetherNet handshake needs the port over both TCP and UDP, are turned away with nothing on
/// either side saying why (issue 108).
/// </para>
/// <para>
/// The rule is by port, not by program: it does not depend on which Java a server uses, and it shows
/// up in the firewall's own list under a name that says where it came from. Changing the firewall
/// needs administrator rights, so the change goes through Windows' own confirmation prompt;
/// reading it does not, which is how the page knows whether the button is needed at all.
/// </para>
/// </remarks>
public sealed class WindowsFirewallService
{
    private const int Tcp = 6, Udp = 17, AnyProtocol = 256;
    private const int EveryNetwork = 7;   // domain | private | public

    private readonly IFirewallBackend _backend;

    public WindowsFirewallService() : this(null) { }

    internal WindowsFirewallService(IFirewallBackend? backend) =>
        _backend = backend ?? (OperatingSystem.IsWindows() ? new WindowsBackend() : new NoBackend());

    /// <summary>The backend of every other platform: there is no firewall to read or change.</summary>
    private sealed class NoBackend : IFirewallBackend
    {
        public IReadOnlyList<FirewallRule> ReadRules() => throw new PlatformNotSupportedException();
        public int RunElevated(string arguments) => throw new PlatformNotSupportedException();
    }

    /// <summary>Whether this platform has a firewall the launcher can manage.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The name of the launcher's rule for one port and protocol, as the firewall lists it.</summary>
    internal static string RuleName(int port, string protocol) =>
        $"MC Server Launcher - Bedrock {port} ({protocol})";

    /// <summary>What the firewall says about <paramref name="port"/>. Reads over a thousand rules: call it off the UI thread.</summary>
    public FirewallState GetState(int port)
    {
        if (!IsSupported) return FirewallState.Unavailable;

        IReadOnlyList<FirewallRule> rules;
        try { rules = _backend.ReadRules(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The firewall service stopped, or a third-party firewall replaced it: nothing to manage.
            return FirewallState.Unavailable;
        }

        var tcp = rules.Where(r => Covers(r, port, Tcp)).ToList();
        var udp = rules.Where(r => Covers(r, port, Udp)).ToList();
        if (tcp.Count == 0 || udp.Count == 0) return FirewallState.Closed;

        var ours = rules.Any(r => r.Name == RuleName(port, "TCP") && Covers(r, port, Tcp))
                   && rules.Any(r => r.Name == RuleName(port, "UDP") && Covers(r, port, Udp));
        return ours ? FirewallState.Open : FirewallState.OpenElsewhere;
    }

    /// <summary>Creates the rules for <paramref name="port"/>, asking Windows for administrator rights.</summary>
    public Task<FirewallOutcome> AllowAsync(int port) =>
        ChangeAsync(AllowArguments(port), port, wantOpen: true);

    /// <summary>Deletes the rules the launcher created for <paramref name="port"/>.</summary>
    public Task<FirewallOutcome> RemoveAsync(int port) =>
        ChangeAsync(RemoveArguments(port), port, wantOpen: false);

    private Task<FirewallOutcome> ChangeAsync(string arguments, int port, bool wantOpen) =>
        Task.Run(() =>
        {
            try { _backend.RunElevated(arguments); }
            catch (OperationCanceledException) { return FirewallOutcome.Cancelled; }
            catch (Exception) { return FirewallOutcome.Failed; }

            // The exit code of a chain of commands says little; the firewall itself says what happened.
            var ours = OwnRulesExist(port);
            return ours == wantOpen ? FirewallOutcome.Done : FirewallOutcome.Failed;
        });

    private bool OwnRulesExist(int port)
    {
        try
        {
            var rules = _backend.ReadRules();
            return rules.Any(r => r.Name == RuleName(port, "TCP"))
                   || rules.Any(r => r.Name == RuleName(port, "UDP"));
        }
        catch (Exception) { return false; }
    }

    // --- the command ---

    /// <summary>
    /// The command line that replaces the rules for one port. Deleting first makes it safe to press
    /// twice, and keeps a rule that was edited by hand from lingering with other settings.
    /// </summary>
    /// <remarks>
    /// The port is the only variable part and it is an int checked against the range, so nothing a
    /// user typed reaches an elevated command line. <c>netsh</c> is resolved from the working
    /// directory the caller pins to System32, not from the search path.
    /// </remarks>
    internal static string AllowArguments(int port)
    {
        CheckPort(port);
        const string description = "description=\"Created by MC Server Launcher so Bedrock players can join\"";
        string Add(string protocol) =>
            $"netsh advfirewall firewall add rule name=\"{RuleName(port, protocol.ToUpperInvariant())}\" " +
            $"dir=in action=allow protocol={protocol} localport={port} profile=any {description}";

        return "/c " + Delete(port) + " & " + Add("tcp") + " && " + Add("udp");
    }

    /// <summary>The command line that deletes the launcher's rules for one port.</summary>
    internal static string RemoveArguments(int port)
    {
        CheckPort(port);
        return "/c " + Delete(port);
    }

    private static string Delete(int port) =>
        $"netsh advfirewall firewall delete rule name=\"{RuleName(port, "TCP")}\" >nul & " +
        $"netsh advfirewall firewall delete rule name=\"{RuleName(port, "UDP")}\" >nul";

    private static void CheckPort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, null);
    }

    // --- reading a rule ---

    /// <summary>Whether <paramref name="rule"/> lets <paramref name="port"/> in over <paramref name="protocol"/> on every network.</summary>
    /// <remarks>
    /// Only rules that name no program and no service count: one for java.exe would let the port in
    /// for that program alone, which is not what the button gives. A rule that skips the public
    /// profile does not count either, because that is the profile that went wrong.
    /// </remarks>
    internal static bool Covers(FirewallRule rule, int port, int protocol) =>
        rule is { Inbound: true, Allow: true, Enabled: true, Narrowed: false }
        && string.IsNullOrEmpty(rule.Application)
        && (string.IsNullOrEmpty(rule.Service) || rule.Service == "*")
        && (rule.Protocol == protocol || rule.Protocol == AnyProtocol)
        && (rule.Profiles & EveryNetwork) == EveryNetwork
        && PortListContains(rule.LocalPorts, port);

    /// <summary>Whether a firewall port list ("*", "80", "1000-2000,3000") includes <paramref name="port"/>.</summary>
    internal static bool PortListContains(string? list, int port)
    {
        if (string.IsNullOrWhiteSpace(list) || list.Trim() == "*") return true;

        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', 2);
            if (!int.TryParse(bounds[0], out var low)) continue;   // "RPC" and the like: not a number
            var high = low;
            if (bounds.Length == 2 && !int.TryParse(bounds[1], out high)) continue;
            if (port >= low && port <= high) return true;
        }
        return false;
    }

    // --- Windows ---

    [SupportedOSPlatform("windows")]
    private sealed class WindowsBackend : IFirewallBackend
    {
        private const int ErrorCancelled = 1223;   // the user answered "no" to the elevation prompt

        public IReadOnlyList<FirewallRule> ReadRules()
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                       ?? throw new InvalidOperationException("The firewall's COM interface is not registered.");
            dynamic policy = Activator.CreateInstance(type)!;

            var rules = new List<FirewallRule>();
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    // Direction 1 is inbound, action 1 is allow. Filtered before the slower reads.
                    if (rule.Direction != 1 || rule.Action != 1 || !rule.Enabled) continue;
                    rules.Add(new FirewallRule(
                        (string)rule.Name, (int)rule.Protocol, (string?)rule.LocalPorts, (int)rule.Profiles,
                        Inbound: true, Allow: true, Enabled: true,
                        (string?)rule.ApplicationName, (string?)rule.ServiceName, IsNarrowed(rule)));
                }
                catch (Exception)
                {
                    // One odd rule (a property that does not apply to its protocol) must not blind the page.
                }
            }
            return rules;
        }

        /// <summary>
        /// Whether a rule is limited by anything besides program and service. Found out the hard way:
        /// the rules Windows keeps for Store apps name no program at all, so without these checks
        /// every port on this machine read as already allowed.
        /// </summary>
        private static bool IsNarrowed(dynamic rule)
        {
            static bool Set(object? value) => value is string { Length: > 0 };
            static bool NotAll(object? value, string all) => value is string { Length: > 0 } s && s != all;

            try
            {
                return Set(rule.LocalAppPackageId) || Set(rule.LocalUserOwner)
                       || Set(rule.LocalUserAuthorizedList) || Set(rule.RemoteUserAuthorizedList)
                       || Set(rule.RemoteMachineAuthorizedList)
                       || NotAll(rule.RemoteAddresses, "*") || NotAll(rule.InterfaceTypes, "All")
                       || NotAll(rule.RemotePorts, "*");
            }
            catch (Exception)
            {
                return true;   // cannot tell: do not claim the port is open on its strength
            }
        }

        public int RunElevated(string arguments)
        {
            var system = Environment.SystemDirectory;
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(system, "cmd.exe"),
                    Arguments = arguments,
                    WorkingDirectory = system,
                    UseShellExecute = true,      // required by Verb, and what makes Windows show the prompt
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                }) ?? throw new InvalidOperationException("The command did not start.");

                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
            {
                throw new OperationCanceledException("The administrator prompt was declined.", e);
            }
        }
    }
}
