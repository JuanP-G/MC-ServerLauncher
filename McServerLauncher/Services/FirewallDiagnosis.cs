namespace McServerLauncher.Services;

/// <summary>Why Bedrock players may be turned away by Windows Firewall.</summary>
public enum FirewallCause
{
    /// <summary>Nothing points at the firewall.</summary>
    None,
    /// <summary>On a connected network, no rule lets the port in for the program that listens on it.</summary>
    NoRule,
    /// <summary>A block rule covers the port or the program on a connected network. It wins over any allow rule.</summary>
    Blocked
}

/// <summary>What the check found: the cause, the networks it applies to, and the rules that block.</summary>
/// <param name="Cause">Why, or <see cref="FirewallCause.None"/>.</param>
/// <param name="Networks">The names of the connected networks that would turn players away.</param>
/// <param name="BlockingRules">The display names of the block rules involved, for the explanation.</param>
public sealed record FirewallFinding(FirewallCause Cause, IReadOnlyList<string> Networks, IReadOnlyList<string> BlockingRules)
{
    /// <summary>The finding that says everything is fine.</summary>
    public static FirewallFinding Nothing { get; } = new(FirewallCause.None, [], []);

    /// <summary>A line that is the same for the same problem, so a dismissal can be remembered against it.</summary>
    public string Signature =>
        Cause == FirewallCause.None
            ? ""
            : Cause + ":" + string.Join("|", Networks.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
}

/// <summary>A program that owns the Bedrock port, and what it needs of it.</summary>
/// <param name="Program">The executable's full path (see <see cref="FirewallDiagnosis.Resolve"/>).</param>
/// <param name="NeedsUdp">Whether it also needs UDP: Java does (the game itself), the sleeping server's listener does not.</param>
public sealed record FirewallOwner(string Program, bool NeedsUdp);

/// <summary>A network the computer is connected to, and the firewall profile Windows gave it.</summary>
/// <param name="Name">The name Windows shows for it ("Red", "Radmin VPN", "Tailscale").</param>
/// <param name="Profile">The profile as a flag: 1 domain, 2 private, 4 public.</param>
internal sealed record FirewallNetwork(string Name, int Profile);

/// <summary>Whether a profile's firewall is on, and whether it drops incoming traffic nobody allowed.</summary>
internal sealed record FirewallProfile(int Profile, bool Enabled, bool BlocksInbound);

/// <summary>Everything the check reads from Windows, in one go.</summary>
internal sealed record FirewallInputs(
    IReadOnlyList<FirewallRule> Allow, IReadOnlyList<FirewallRule> Block,
    IReadOnlyList<FirewallProfile> Profiles, IReadOnlyList<FirewallNetwork> Networks);

/// <summary>
/// Decides from the firewall's rules whether Bedrock players on a connected network would be turned
/// away. Pure: it reads nothing, which is what makes it testable without a firewall.
/// </summary>
/// <remarks>
/// <para>
/// The launcher cannot see a dropped packet, so this deduces: a network the machine is connected to,
/// whose profile has the firewall on and drops unsolicited incoming traffic, and no rule that lets
/// the port in for the program that listens on it. That is exactly the case of issue 108: the rule
/// Windows made for java.exe when it asked "allow?" covered the private profile only, and the VPN
/// was public.
/// </para>
/// <para>
/// A block rule is reported apart, because adding an allow rule would not help: block wins. Windows
/// makes such rules itself when the "allow?" prompt is turned down, one per program and profile.
/// </para>
/// </remarks>
internal static class FirewallDiagnosis
{
    private const int Tcp = 6, Udp = 17, AnyProtocol = 256;

    internal static FirewallFinding Diagnose(int port, IReadOnlyList<FirewallOwner> owners, FirewallInputs inputs)
    {
        var networks = new List<string>();
        var blocking = new List<string>();
        var blocked = false;

        foreach (var network in inputs.Networks)
        {
            var profile = inputs.Profiles.FirstOrDefault(p => p.Profile == network.Profile);
            // A profile with the firewall off, or one that lets everything in, turns nobody away.
            if (profile is { Enabled: false } or { BlocksInbound: false }) continue;

            var missing = false;
            var blockedHere = false;
            foreach (var owner in owners)
            {
                foreach (var protocol in owner.NeedsUdp ? new[] { Tcp, Udp } : new[] { Tcp })
                {
                    var block = inputs.Block.Where(r => Applies(r, port, protocol, network.Profile, owner.Program)).ToList();
                    if (block.Count > 0)
                    {
                        blockedHere = true;
                        foreach (var rule in block)
                            if (!blocking.Contains(rule.Name)) blocking.Add(rule.Name);
                    }
                    else if (!inputs.Allow.Any(r => Applies(r, port, protocol, network.Profile, owner.Program)))
                        missing = true;
                }
            }

            if (!blockedHere && !missing) continue;
            blocked |= blockedHere;
            if (!networks.Contains(network.Name)) networks.Add(network.Name);
        }

        if (networks.Count == 0) return FirewallFinding.Nothing;
        return new FirewallFinding(blocked ? FirewallCause.Blocked : FirewallCause.NoRule, networks, blocking);
    }

    /// <summary>Whether <paramref name="rule"/> (an allow or a block, they read alike) concerns this traffic.</summary>
    internal static bool Applies(FirewallRule rule, int port, int protocol, int profile, string program) =>
        rule is { Inbound: true, Enabled: true, Narrowed: false }
        && (string.IsNullOrEmpty(rule.Service) || rule.Service == "*")
        && (rule.Protocol == protocol || rule.Protocol == AnyProtocol)
        && (rule.Profiles & profile) != 0
        && WindowsFirewallService.PortListContains(rule.LocalPorts, port)
        && (string.IsNullOrEmpty(rule.Application) || SameProgram(rule.Application, program));

    /// <summary>Whether a rule's program is the one that listens: the same file, however it is spelled.</summary>
    internal static bool SameProgram(string rulePath, string program) =>
        string.Equals(Full(Environment.ExpandEnvironmentVariables(rulePath)), Full(program),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file a launch command really runs: a path as it is, a bare name ("java") through the
    /// PATH as Windows would. Null when it cannot be found, and then there is no program to check
    /// rules against — comparing file names alone matched whichever java.exe on the machine
    /// happened to have a rule.
    /// </summary>
    internal static string? Resolve(string program, string? pathVariable = null)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        if (Path.IsPathRooted(program)) return File.Exists(program) ? Full(program) : null;

        var name = program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? program : program + ".exe";
        foreach (var directory in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return Full(candidate);
            }
            catch (Exception) { /* a malformed PATH entry */ }
        }
        return null;
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    /// <summary>The profile flag for the category the Network List Manager reports (0 public, 1 private, 2 domain).</summary>
    internal static int ProfileOf(int category) => category switch { 1 => 2, 2 => 1, _ => 4 };
}
