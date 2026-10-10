using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>How one stretch of the connection did.</summary>
public enum StepState { Waiting, Running, Good, Fair, Bad, Skipped }

/// <summary>One stretch of the way a player's packets travel, and how it did.</summary>
public partial class ConnectionStepViewModel : ObservableObject
{
    public ConnectionStepViewModel(string title) => _title = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(IsGood), nameof(IsFair), nameof(IsBad), nameof(IsRunning), nameof(IsIdle))]
    private StepState _state;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _detail = string.Empty;

    /// <summary>The number beside it: "3 ms", "↑ 42 Mb/s".</summary>
    [ObservableProperty]
    private string _badge = string.Empty;

    public Symbol Icon => State switch
    {
        StepState.Good => Symbol.Checkmark,
        StepState.Fair => Symbol.Warning,
        StepState.Bad => Symbol.ErrorCircle,
        StepState.Running => Symbol.ArrowSync,
        StepState.Skipped => Symbol.Subtract,
        _ => Symbol.Circle,
    };

    public bool IsGood => State == StepState.Good;
    public bool IsFair => State == StepState.Fair;
    public bool IsBad => State == StepState.Bad;
    public bool IsRunning => State == StepState.Running;
    public bool IsIdle => State is StepState.Waiting or StepState.Skipped;

    internal void Set(StepState state, string title, string detail, string badge = "")
    {
        State = state;
        Title = title;
        Detail = detail;
        Badge = badge;
    }
}

/// <summary>The verdict's colour, from its state.</summary>
public static class StepStateConverters
{
    public static readonly Avalonia.Data.Converters.FuncValueConverter<StepState, bool> IsFair = new(s => s == StepState.Fair);
    public static readonly Avalonia.Data.Converters.FuncValueConverter<StepState, bool> IsBad = new(s => s == StepState.Bad);
}

/// <summary>One row of the technical table: the round trips to one place.</summary>
public sealed record ProbeRow(string Name, string Min, string Average, string Max, string Jitter, string Loss)
{
    /// <summary>Milliseconds with a decimal under ten: on a LAN "0" says nothing, "0.3" does.</summary>
    internal static string Number(double ms) =>
        ms.ToString(ms < 10 ? "0.0" : "0", CultureInfo.CurrentCulture);

    internal static ProbeRow From(string name, LatencyStats stats)
    {
        static string Ms(double v) => Number(v);
        return stats.AnyReply
            ? new(name, Ms(stats.Min), Ms(stats.Average), Ms(stats.Max), Ms(stats.Jitter),
                (stats.Loss * 100).ToString("0", CultureInfo.CurrentCulture) + " %")
            : new(name, "—", "—", "—", "—", stats.Sent > 0 ? "100 %" : "—");
    }
}

/// <summary>
/// The connection test on a server's network page: where a slow game is coming from.
/// </summary>
/// <remarks>
/// <para>
/// It measures the stretches a player's packets cross one at a time — the server alone, this
/// machine's internet, the Playit tunnel for Java, and the one for Bedrock — and each lights up as it
/// finishes, with a sentence that says what it means. Whoever knows nothing about networks reads the
/// sentences; whoever does opens the details, with every round trip's minimum, average, maximum,
/// jitter and loss, the line's speed and peak, the samples, and a report to copy.
/// </para>
/// <para>
/// With the server off only the internet can be measured; the test says so, and offers to start the
/// server and run again. It never starts it by itself.
/// </para>
/// </remarks>
public partial class ConnectionTestViewModel : ObservableObject
{
    private const int Samples = 8;
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    // Whether the details were left open: for as long as the app runs, on every server.
    private static bool _detailsOpen;

    private readonly ServerViewModel _server;
    private readonly ServerPropertiesService _properties = new();
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _report = new();

    public ConnectionTestViewModel(ServerViewModel server)
    {
        _server = server;
        LocalStep = new ConnectionStepViewModel(Localizer.Get("Net_StepLocal"));
        InternetStep = new ConnectionStepViewModel(Localizer.Get("Net_StepInternet"));
        TunnelStep = new ConnectionStepViewModel(Localizer.Get("Net_StepTunnel"));
        BedrockStep = new ConnectionStepViewModel(Localizer.Get("Net_StepBedrock"));
        Steps.Add(LocalStep);
        Steps.Add(InternetStep);
        Steps.Add(TunnelStep);
        if (server.IsCrossplayOn) Steps.Add(BedrockStep);
        Reset();
    }

    public ObservableCollection<ConnectionStepViewModel> Steps { get; } = new();
    public ConnectionStepViewModel LocalStep { get; }
    public ConnectionStepViewModel InternetStep { get; }
    public ConnectionStepViewModel TunnelStep { get; }
    public ConnectionStepViewModel BedrockStep { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StartServerAndRunCommand), nameof(CopyReportCommand))]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    private bool _hasRun;

    public string RunButtonText => Localizer.Get(IsTesting ? "Net_Testing" : HasRun ? "Net_Repeat" : "Net_Run");

    /// <summary>One sentence for the whole test, once it has run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerdict))]
    private string _verdict = string.Empty;

    [ObservableProperty]
    private StepState _verdictState;

    public bool HasVerdict => Verdict.Length > 0;

    /// <summary>The last run could not test everything, because the server was off.</summary>
    [ObservableProperty]
    private bool _serverWasOff;

    public bool DetailsOpen
    {
        get => _detailsOpen;
        set
        {
            if (_detailsOpen == value) return;
            _detailsOpen = value;
            OnPropertyChanged();
        }
    }

    // ---- The details ----

    public ObservableCollection<ProbeRow> Rows { get; } = new();

    [ObservableProperty]
    private string _downloadText = "—";

    [ObservableProperty]
    private string _uploadText = "—";

    [ObservableProperty]
    private string _peakText = "—";

    /// <summary>The tunnel's round trips, or the server's when there is no tunnel, for the little graph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSamples))]
    private IReadOnlyList<double> _samplesSeries = Array.Empty<double>();

    public bool HasSamples => SamplesSeries.Count > 1;

    [ObservableProperty]
    private string _samplesTitle = string.Empty;

    [ObservableProperty]
    private string _addressesText = string.Empty;

    [ObservableProperty]
    private string _copyText = Localizer.Get("Net_CopyReport");

    private void Reset()
    {
        foreach (var step in Steps)
            step.Set(StepState.Waiting, step.Title, Localizer.Get("Net_Waiting"));
        Verdict = string.Empty;
        ServerWasOff = false;
    }

    private bool CanRun => !IsTesting;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task Run() => RunAsync();

    /// <summary>Starts the server, waits until it answers, and runs the whole test.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartServerAndRun()
    {
        if (_server.State != ServerState.Running && _server.StartCommand.CanExecute(null))
        {
            IsTesting = true;
            LocalStep.Set(StepState.Running, Localizer.Get("Net_Starting"), Localizer.Get("Net_StartingDetail"));
            _server.StartCommand.Execute(null);

            // Up to three minutes: a modded server can take that long to open its port. Back to
            // stopped after the first seconds means it did not start, and there is no point waiting.
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromMinutes(3) && _server.State != ServerState.Running)
            {
                if (_server.State == ServerState.Stopped && DateTime.UtcNow - started > TimeSpan.FromSeconds(5)) break;
                await Task.Delay(500);
            }
            IsTesting = false;
        }
        await RunAsync();
    }

    /// <summary>Runs every stretch in turn.</summary>
    internal async Task RunAsync()
    {
        if (IsTesting) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Crossplay may have been switched on or off since the last run.
        if (_server.IsCrossplayOn && !Steps.Contains(BedrockStep)) Steps.Add(BedrockStep);
        if (!_server.IsCrossplayOn) Steps.Remove(BedrockStep);

        IsTesting = true;
        Reset();
        Rows.Clear();
        DownloadText = UploadText = PeakText = "—";
        SamplesSeries = Array.Empty<double>();
        _report.Clear();
        CopyText = Localizer.Get("Net_CopyReport");

        var running = _server.State == ServerState.Running;
        ServerWasOff = !running;
        var port = _properties.GetServerPort(_server.Config.PropertiesPath) ?? 25565;
        var addresses = new List<string> { $"{Localizer.Get("Net_RowLocal")}: 127.0.0.1:{port}" };
        _report.AppendLine($"MC Server Launcher — {_server.Name} — {DateTime.Now:g}");

        try
        {
            // ---- The server alone ----
            LatencyStats local = LatencyStats.None;
            SlpReply? status = null;
            if (!running)
            {
                LocalStep.Set(StepState.Skipped, Localizer.Get("Net_LocalOff"), Localizer.Get("Net_LocalOffDetail"));
            }
            else
            {
                LocalStep.Set(StepState.Running, Localizer.Get("Net_StepLocal"), Localizer.Get("Net_Measuring"));
                local = await ConnectionProbe.SampleAsync(async c =>
                {
                    var reply = await ConnectionProbe.PingJavaAsync("127.0.0.1", port, Wait, c);
                    status ??= reply;
                    return reply?.LatencyMs;
                }, Samples, Gap, Live(LocalStep), ct);
                Judge(LocalStep, local, good: 30, fair: 100, "Net_Local");
                Rows.Add(ProbeRow.From(Localizer.Get("Net_RowLocal"), local));
            }

            // ---- This machine's internet ----
            InternetStep.Set(StepState.Running, Localizer.Get("Net_StepInternet"), Localizer.Get("Net_Measuring"));
            var internet = await ConnectionProbe.SampleAsync(
                c => ConnectionProbe.TcpRoundTripAsync(ConnectionProbe.LatencyHost, 443, Wait, c),
                Samples, TimeSpan.FromMilliseconds(100), Live(InternetStep), ct);
            Rows.Add(ProbeRow.From(Localizer.Get("Net_RowInternet"), internet));

            Throughput down = Throughput.None, up = Throughput.None;
            if (internet.AnyReply)
            {
                InternetStep.Detail = Localizer.Get("Net_MeasuringDown");
                down = await ConnectionProbe.DownloadAsync(25_000_000, TimeSpan.FromSeconds(6), ct);
                DownloadText = Mbps(down);
                InternetStep.Badge = "↓ " + DownloadText;
                InternetStep.Detail = Localizer.Get("Net_MeasuringUp");
                up = await ConnectionProbe.UploadAsync(10_000_000, TimeSpan.FromSeconds(6), ct);
                UploadText = Mbps(up);
                PeakText = Bytes(up.PeakBytesPerSecond) + "/s";
            }
            JudgeInternet(internet, up);

            // ---- The Java tunnel ----
            LatencyStats tunnel = LatencyStats.None;
            var address = _server.Config.PlayitEnabled ? _server.TunnelAddress : null;
            if (string.IsNullOrWhiteSpace(address))
                TunnelStep.Set(StepState.Skipped, Localizer.Get("Net_NoTunnel"), Localizer.Get("Net_NoTunnelDetail"));
            else if (!running)
                TunnelStep.Set(StepState.Skipped, Localizer.Get("Net_StepTunnel"), Localizer.Get("Net_NeedsServer"));
            else
            {
                TunnelStep.Set(StepState.Running, Localizer.Get("Net_StepTunnel"), Localizer.Get("Net_Measuring"));
                var (host, publicPort) = await ConnectionProbe.ResolveJavaAsync(address, ct);
                addresses.Add($"{Localizer.Get("Net_RowTunnel")}: {address} → {host}:{publicPort}");
                // The server's own protocol number: Playit's edge refuses the -1 status tools send.
                var protocol = status?.Protocol is > 0 and var known ? known : ConnectionProbe.FallbackProtocol;
                tunnel = await ConnectionProbe.SampleAsync(async c =>
                    (await ConnectionProbe.PingJavaAsync(host, publicPort, Wait, c, protocol))?.LatencyMs,
                    Samples, Gap, Live(TunnelStep), ct);
                Judge(TunnelStep, tunnel, good: 80, fair: 180, "Net_Tunnel");
                Rows.Add(ProbeRow.From(Localizer.Get("Net_RowTunnel"), tunnel));
            }

            // ---- The Bedrock tunnel ----
            if (Steps.Contains(BedrockStep))
            {
                var host = _server.BedrockHost;
                if (string.IsNullOrWhiteSpace(host) || !int.TryParse(_server.BedrockPortText, out var bedrockPort))
                    BedrockStep.Set(StepState.Skipped, Localizer.Get("Net_NoBedrock"), Localizer.Get("Net_NoBedrockDetail"));
                else if (!running)
                    BedrockStep.Set(StepState.Skipped, Localizer.Get("Net_StepBedrock"), Localizer.Get("Net_NeedsServer"));
                else
                {
                    BedrockStep.Set(StepState.Running, Localizer.Get("Net_StepBedrock"), Localizer.Get("Net_Measuring"));
                    addresses.Add($"{Localizer.Get("Net_RowBedrock")}: {host}:{bedrockPort}");
                    var bedrock = await ConnectionProbe.SampleAsync(
                        c => ConnectionProbe.PingBedrockAsync(host, bedrockPort, Wait, c),
                        Samples, Gap, Live(BedrockStep), ct);
                    Judge(BedrockStep, bedrock, good: 80, fair: 180, "Net_Bedrock");
                    Rows.Add(ProbeRow.From(Localizer.Get("Net_RowBedrock"), bedrock));
                }
            }

            var series = tunnel.AnyReply ? tunnel : local;
            SamplesSeries = series.Samples.ToList();
            SamplesTitle = string.Format(Localizer.Get(tunnel.AnyReply ? "Net_SamplesTunnelFmt" : "Net_SamplesLocalFmt"),
                series.Received);

            if (status is not null)
                // Not its protocol number: some servers report back whatever the ping asked with.
                addresses.Add(string.Format(Localizer.Get("Net_ServerInfoFmt"), status.Version,
                    status.Online, status.Max));
            AddressesText = string.Join("   ·   ", addresses);

            Conclude();
            WriteReport(addresses, down, up);
        }
        catch (OperationCanceledException)
        {
            Reset();
        }
        finally
        {
            IsTesting = false;
            HasRun = true;
        }
    }

    /// <summary>Stops a test in progress: the page is going away.</summary>
    internal void Cancel() => _cts?.Cancel();

    // ---------------------------------------------------------------- judging

    /// <summary>Shows each round trip in the badge as it arrives, so the step visibly works.</summary>
    /// <remarks>
    /// Only while the step is still running: Progress posts, so the last sample can arrive after the
    /// step has been judged, and it used to overwrite the average with "1 ms…".
    /// </remarks>
    private static IProgress<double> Live(ConnectionStepViewModel step) =>
        new Progress<double>(ms =>
        {
            if (step.State == StepState.Running) step.Badge = Ms(ms) + "…";
        });

    /// <summary>Good, fair or bad by the average round trip; loss makes it worse.</summary>
    internal static StepState Grade(LatencyStats stats, double good, double fair)
    {
        if (!stats.AnyReply) return StepState.Bad;
        var state = stats.Average < good ? StepState.Good : stats.Average < fair ? StepState.Fair : StepState.Bad;
        if (stats.Loss > 0.25) return StepState.Bad;
        if (stats.Loss > 0 && state == StepState.Good) return StepState.Fair;
        return state;
    }

    private static void Judge(ConnectionStepViewModel step, LatencyStats stats, double good, double fair, string prefix)
    {
        var state = Grade(stats, good, fair);
        var key = !stats.AnyReply ? "NoReply" : state.ToString();
        step.Set(state, Localizer.Get($"{prefix}{key}"), Localizer.Get($"{prefix}{key}Detail"),
            stats.AnyReply ? Ms(stats.Average) : Localizer.Get("Net_NoAnswer"));
    }

    private void JudgeInternet(LatencyStats latency, Throughput up)
    {
        if (!latency.AnyReply)
        {
            InternetStep.Set(StepState.Bad, Localizer.Get("Net_InternetNone"), Localizer.Get("Net_InternetNoneDetail"),
                Localizer.Get("Net_NoAnswer"));
            return;
        }

        var byLatency = Grade(latency, good: 50, fair: 120);
        var byUpload = up.Mbps <= 0 ? StepState.Fair : up.Mbps < 3 ? StepState.Bad : up.Mbps < 10 ? StepState.Fair : StepState.Good;
        var state = (StepState)Math.Max((int)byLatency, (int)byUpload);
        // About one megabit a player, which is generous: past a hundred the number stops meaning much.
        var players = Math.Clamp((int)Math.Floor(up.Mbps), 1, 100);

        var (title, detail) = state switch
        {
            StepState.Good => ("Net_InternetGood", string.Format(Localizer.Get("Net_InternetGoodDetailFmt"), players)),
            StepState.Fair when byUpload >= byLatency && up.Mbps > 0 =>
                ("Net_InternetFair", string.Format(Localizer.Get("Net_InternetSlowUpFmt"), players)),
            StepState.Fair => ("Net_InternetFair", Localizer.Get("Net_InternetLaggyDetail")),
            _ => ("Net_InternetBad", Localizer.Get("Net_InternetBadDetail")),
        };
        InternetStep.Set(state, Localizer.Get(title), detail,
            up.Mbps > 0 ? "↑ " + Mbps(up) + " · " + Ms(latency.Average) : Ms(latency.Average));
    }

    private void Conclude()
    {
        var measured = Steps.Where(s => s.State is StepState.Good or StepState.Fair or StepState.Bad).ToList();
        var worst = measured.OrderByDescending(s => (int)s.State).FirstOrDefault();
        if (worst is null || worst.State == StepState.Good)
        {
            VerdictState = StepState.Good;
            Verdict = Localizer.Get(ServerWasOff ? "Net_VerdictPartial" : "Net_VerdictGood");
            return;
        }
        VerdictState = worst.State;
        Verdict = string.Format(Localizer.Get("Net_VerdictFmt"), worst.Title, worst.Detail);
    }

    // ---------------------------------------------------------------- report

    private void WriteReport(List<string> addresses, Throughput down, Throughput up)
    {
        foreach (var step in Steps)
            _report.AppendLine($"[{step.State}] {step.Title} — {step.Badge} — {step.Detail}");
        _report.AppendLine();
        _report.AppendLine("min / avg / max / jitter / loss (ms)");
        foreach (var row in Rows)
            _report.AppendLine($"{row.Name}: {row.Min} / {row.Average} / {row.Max} / {row.Jitter} / {row.Loss}");
        _report.AppendLine($"↓ {Mbps(down)}   ↑ {Mbps(up)}   peak ↑ {PeakText}");
        foreach (var a in addresses) _report.AppendLine(a);
        _report.AppendLine(Verdict);
    }

    private bool CanCopy => !IsTesting && _report.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyReport()
    {
        var top = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top?.Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(_report.ToString());
            CopyText = Localizer.Get("Net_Copied");
        }
        catch { /* clipboard busy */ }
    }

    // ---------------------------------------------------------------- formats

    private static string Ms(double ms) => ProbeRow.Number(ms) + " ms";

    private static string Mbps(Throughput t) =>
        t.Mbps <= 0 ? "—" : t.Mbps.ToString(t.Mbps < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " Mb/s";

    private static string Bytes(double bytes) =>
        bytes >= 1_000_000 ? (bytes / 1_000_000).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
        : (bytes / 1_000).ToString("0", CultureInfo.CurrentCulture) + " kB";
}
