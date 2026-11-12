using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The Windows Firewall notice: shown only when the firewall would really turn Bedrock players away,
/// with the way out next to it.
/// </summary>
/// <remarks>
/// The launcher cannot see a dropped packet, so the check deduces (see <see cref="FirewallDiagnosis"/>)
/// and is made when it can be wrong least: when the page opens, after a start once Geyser has had
/// time to listen, and when crossplay or the port change. A server that runs without listening yet is
/// never blamed on the firewall.
/// </remarks>
public partial class ServerViewModel
{
    private static readonly TimeSpan[] FirewallRechecks = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)];

    private FirewallFinding _firewallFinding = FirewallFinding.Nothing;
    private int _firewallChecks;

    /// <summary>The firewall, readable and changeable. Replaceable so a test need not touch Windows'.</summary>
    internal WindowsFirewallService Firewall { get; set; } = new();

    /// <summary>True while Windows' confirmation prompt is open on the notice's account.</summary>
    [ObservableProperty]
    private bool _firewallBusy;

    /// <summary>How the last attempt to fix it ended, when it did not work. Empty otherwise.</summary>
    [ObservableProperty]
    private string _firewallFixNote = "";

    partial void OnFirewallBusyChanged(bool value) => OnPropertyChanged(nameof(FirewallFixLabel));

    partial void OnFirewallFixNoteChanged(string value) => OnPropertyChanged(nameof(FirewallNoticeText));

    /// <summary>
    /// Whether to show the notice: crossplay is on, the check found a cause, and it is not the one
    /// the owner already said they do not mind.
    /// </summary>
    public bool ShowFirewallNotice =>
        IsCrossplayOn
        && _firewallFinding.Cause != FirewallCause.None
        && _firewallFinding.Signature != (Config.FirewallNoticeDismissed ?? "");

    /// <summary>What is wrong, naming the networks and the port, then how the last attempt went if it failed.</summary>
    public string FirewallNoticeText
    {
        get
        {
            if (_firewallFinding.Cause == FirewallCause.None) return "";
            var networks = string.Join(", ", _firewallFinding.Networks);
            var port = CrossplayService.EffectiveBedrockPort(Config) ?? Config.BedrockPort;
            var text = _firewallFinding.Cause == FirewallCause.Blocked
                ? Localizer.Format("Firewall_NoticeBlockedFmt", networks, port,
                    string.Join(", ", _firewallFinding.BlockingRules))
                : Localizer.Format("Firewall_NoticeFmt", networks, port);
            return FirewallFixNote.Length == 0 ? text : text + " " + FirewallFixNote;
        }
    }

    /// <summary>The button: create the permission when nothing allows the port, open Windows' console when a rule blocks it.</summary>
    public string FirewallFixLabel =>
        Localizer.Get(FirewallBusy ? "Firewall_Working"
            : _firewallFinding.Cause == FirewallCause.Blocked ? "Firewall_OpenConsole" : "Firewall_Allow");

    /// <summary>Looks at the firewall again and updates the notice. Safe to call from anywhere on the UI thread.</summary>
    internal async Task RefreshFirewallNoticeAsync()
    {
        var port = CrossplayService.EffectiveBedrockPort(Config);
        if (!WindowsFirewallService.IsSupported || !IsCrossplayOn || port is not { } p)
        {
            SetFirewallFinding(FirewallFinding.Nothing);
            return;
        }

        // Starting and stopping change who owns the port from one moment to the next: wait for the end.
        var state = State;
        if (state is ServerState.Starting or ServerState.Stopping) return;

        var owners = FirewallOwners(state);
        var check = ++_firewallChecks;
        var finding = await Task.Run(() =>
            // Up but not answering on the port yet: Geyser is still starting, not the firewall's doing.
            state == ServerState.Running && !WindowsFirewallService.IsListening(p)
                ? FirewallFinding.Nothing
                : Firewall.Diagnose(p, owners));

        if (check == _firewallChecks) SetFirewallFinding(finding);   // a newer check already answered
    }

    /// <summary>
    /// Who has the port now: the server's Java while it runs, and also the launcher itself while it
    /// sleeps waiting for a Bedrock player, which listens on that port to wake it.
    /// </summary>
    private List<FirewallOwner> FirewallOwners(ServerState state)
    {
        var owners = new List<FirewallOwner>();
        if (FirewallDiagnosis.Resolve(Config.JavaPath) is { } java) owners.Add(new FirewallOwner(java, NeedsUdp: true));
        if (state == ServerState.Stopped && Config.WakeOnDemand && Environment.ProcessPath is { } self)
            owners.Add(new FirewallOwner(self, NeedsUdp: true));   // RakNetWakeListener answers on UDP
        return owners;
    }

    private void SetFirewallFinding(FirewallFinding finding)
    {
        _firewallFinding = finding;
        if (finding.Cause == FirewallCause.None) FirewallFixNote = "";
        OnPropertyChanged(nameof(ShowFirewallNotice));
        OnPropertyChanged(nameof(FirewallNoticeText));
        OnPropertyChanged(nameof(FirewallFixLabel));
    }

    /// <summary>After a start: Geyser needs a moment before it listens, and the first look would only see "not yet".</summary>
    private async Task RecheckFirewallAfterStartAsync()
    {
        foreach (var wait in FirewallRechecks)
        {
            await Task.Delay(wait);
            if (State != ServerState.Running) return;
            await RefreshFirewallNoticeAsync();
        }
    }

    /// <summary>Creates the permission, or opens the firewall's console when a rule is in the way.</summary>
    [RelayCommand]
    private async Task FixFirewallAsync()
    {
        if (FirewallBusy) return;

        if (_firewallFinding.Cause == FirewallCause.Blocked)
        {
            // A block rule is Windows' own, or somebody's: looked at in its console, not deleted from here.
            WindowsFirewallService.OpenConsole();
            return;
        }
        if (CrossplayService.EffectiveBedrockPort(Config) is not { } port) return;

        FirewallBusy = true;
        FirewallFixNote = "";
        try
        {
            var outcome = await Firewall.AllowAsync(port);
            if (outcome == FirewallOutcome.Cancelled) FirewallFixNote = Localizer.Get("Firewall_Cancelled");
            else if (outcome == FirewallOutcome.Failed) FirewallFixNote = Localizer.Get("Firewall_Failed");
        }
        finally
        {
            FirewallBusy = false;
        }
        await RefreshFirewallNoticeAsync();
    }

    /// <summary>Remembers this problem as not mattering: the notice stays away until a different one shows up.</summary>
    [RelayCommand]
    private void DismissFirewallNotice()
    {
        Config.FirewallNoticeDismissed = _firewallFinding.Signature;
        ConfigChanged?.Invoke();
        OnPropertyChanged(nameof(ShowFirewallNotice));
    }
}
