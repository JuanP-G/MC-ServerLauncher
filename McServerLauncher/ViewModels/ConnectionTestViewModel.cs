using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

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
public sealed partial class ConnectionTestViewModel : ObservableObject, IDisposable
{
    private const int Samples = 8;
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    // Whether the details were left open: for as long as the app runs, on every server.
    private static bool DetailsLeftOpen;

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
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StartServerAndRunCommand),
        nameof(CopyReportCommand))]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    private bool _hasRun;

    public string RunButtonText => Localizer.Get(IsTesting ? "Net_Testing" : HasRun ? "Net_Repeat"
        : "Net_Run");

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
        get => DetailsLeftOpen;
        set
        {
            if (DetailsLeftOpen == value) return;
            DetailsLeftOpen = value;
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

    /// <summary>
    /// The tunnel's round trips, or the server's when there is no tunnel, for the little graph.
    /// </summary>
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
            step.Reset(Localizer.Get("Net_Waiting"));
        Verdict = string.Empty;
        ServerWasOff = false;
    }

    private bool CanRun => !IsTesting;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task Run() => RunAsync();

    /// <summary>Starts the server, waits until it answers, and runs the whole test.</summary>
    /// <remarks>
    /// A start spends its first seconds — minutes, with a big world to back up — getting ready, and
    /// all that time the server still reads as stopped. The wait used to give up after five seconds
    /// of "stopped" and test a server that was about to come up, and say it was off. A start already
    /// under way (the button, or waking on demand) is waited for in the same way, not started twice.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartServerAndRun()
    {
        var startsOrIsStarting = _server.IsPreparing || _server.StartCommand.CanExecute(null);
        if (_server.State != ServerState.Running && startsOrIsStarting)
        {
            var ct = Renew();
            IsTesting = true;
            LocalStep.Set(StepState.Running, Localizer.Get("Net_Starting"),
                Localizer.Get("Net_StartingDetail"));
            if (!_server.IsPreparing) _server.StartCommand.Execute(null);
            try
            {
                await WaitUntilRunningAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Reset();
                return;
            }
            finally
            {
                IsTesting = false;
            }
        }
        await RunAsync();
    }

    /// <summary>
    /// Up to three minutes: a modded server can take that long to open its port. Stopped and no
    /// longer getting ready, past the first seconds, means it did not start, and there is no point
    /// waiting.
    /// </summary>
    private async Task WaitUntilRunningAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(3) && _server.State != ServerState.Running)
        {
            var gaveUp = _server.State == ServerState.Stopped && !_server.IsPreparing;
            if (gaveUp && DateTime.UtcNow - started > TimeSpan.FromSeconds(5)) break;
            await Task.Delay(500, ct);
        }
    }

    /// <summary>What the stretches of one run hand on to the next and to the report.</summary>
    private sealed class RunState
    {
        public bool ServerRunning;
        public int Port;
        public SlpReply? Status;
        public LatencyStats Local = LatencyStats.None;
        public LatencyStats Tunnel = LatencyStats.None;
        public Throughput Down = Throughput.None;
        public Throughput Up = Throughput.None;
        public readonly List<string> Addresses = new();
    }

    /// <summary>Runs every stretch in turn.</summary>
    internal async Task RunAsync()
    {
        if (IsTesting) return;
        var ct = Renew();

        var run = BeginRun();
        try
        {
            await TestLocalServerAsync(run, ct);
            await TestInternetAsync(run, ct);
            await TestJavaTunnelAsync(run, ct);
            await TestBedrockTunnelAsync(run, ct);

            ShowSamples(run);
            Conclude();
            WriteReport(run.Addresses, run.Down, run.Up);
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

    /// <summary>Clears the last run from the screen and notes what this one starts from.</summary>
    private RunState BeginRun()
    {
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

        var run = new RunState
        {
            ServerRunning = _server.State == ServerState.Running,
            Port = _properties.GetServerPort(_server.Config.PropertiesPath) ?? 25565
        };
        ServerWasOff = !run.ServerRunning;
        run.Addresses.Add($"{Localizer.Get("Net_RowLocal")}: 127.0.0.1:{run.Port}");
        _report.AppendLine($"MC Server Launcher — {_server.Name} — {DateTime.Now:g}");
        return run;
    }

    /// <summary>The server alone: pings it on this machine, which also tells its protocol number.</summary>
    private async Task TestLocalServerAsync(RunState run, CancellationToken ct)
    {
        if (!run.ServerRunning)
        {
            LocalStep.Set(StepState.Skipped, Localizer.Get("Net_LocalOff"),
                Localizer.Get("Net_LocalOffDetail"));
            return;
        }

        LocalStep.Set(StepState.Running, Localizer.Get("Net_StepLocal"), Localizer.Get("Net_Measuring"));
        run.Local = await ConnectionProbe.SampleAsync(async c =>
        {
            var reply = await ConnectionProbe.PingJavaAsync("127.0.0.1", run.Port, Wait, ct: c);
            run.Status ??= reply;
            return reply?.LatencyMs;
        }, Samples, Gap, Live(LocalStep), ct);
        Judge(LocalStep, run.Local, good: 30, fair: 100, "Net_Local");
        Rows.Add(ProbeRow.From(Localizer.Get("Net_RowLocal"), run.Local));
    }

    /// <summary>This machine's internet: latency, then download and upload if anything answered.</summary>
    private async Task TestInternetAsync(RunState run, CancellationToken ct)
    {
        InternetStep.Set(StepState.Running, Localizer.Get("Net_StepInternet"), Localizer.Get("Net_Measuring"));
        var internet = await ConnectionProbe.SampleAsync(
            c => ConnectionProbe.TcpRoundTripAsync(ConnectionProbe.LatencyHost, 443, Wait, c),
            Samples, TimeSpan.FromMilliseconds(100), Live(InternetStep), ct);
        Rows.Add(ProbeRow.From(Localizer.Get("Net_RowInternet"), internet));

        if (internet.AnyReply)
        {
            InternetStep.Detail = Localizer.Get("Net_MeasuringDown");
            run.Down = await ConnectionProbe.DownloadAsync(25_000_000, TimeSpan.FromSeconds(6), ct);
            DownloadText = Mbps(run.Down);
            InternetStep.Badge = "↓ " + DownloadText;
            InternetStep.Detail = Localizer.Get("Net_MeasuringUp");
            run.Up = await ConnectionProbe.UploadAsync(10_000_000, TimeSpan.FromSeconds(6), ct);
            UploadText = Mbps(run.Up);
            PeakText = Bytes(run.Up.PeakBytesPerSecond) + "/s";
        }
        JudgeInternet(internet, run.Up);
    }

    /// <summary>The Java tunnel: pings the public address the way a player's game would.</summary>
    private async Task TestJavaTunnelAsync(RunState run, CancellationToken ct)
    {
        var address = _server.Config.PlayitEnabled ? _server.TunnelAddress : null;
        if (string.IsNullOrWhiteSpace(address))
        {
            TunnelStep.Set(StepState.Skipped, Localizer.Get("Net_NoTunnel"), Localizer.Get("Net_NoTunnelDetail"));
            return;
        }
        if (!run.ServerRunning)
        {
            TunnelStep.Set(StepState.Skipped, Localizer.Get("Net_StepTunnel"), Localizer.Get("Net_NeedsServer"));
            return;
        }

        TunnelStep.Set(StepState.Running, Localizer.Get("Net_StepTunnel"), Localizer.Get("Net_Measuring"));
        var (host, publicPort) = await ConnectionProbe.ResolveJavaAsync(address, ct);
        run.Addresses.Add($"{Localizer.Get("Net_RowTunnel")}: {address} → {host}:{publicPort}");

        // The server's own protocol number: Playit's edge refuses the -1 status tools send.
        var protocol = run.Status?.Protocol is > 0 and var known ? known : ConnectionProbe.FallbackProtocol;
        run.Tunnel = await ConnectionProbe.SampleAsync(async c =>
            (await ConnectionProbe.PingJavaAsync(host, publicPort, Wait, protocol, c))?.LatencyMs,
            Samples, Gap, Live(TunnelStep), ct);
        Judge(TunnelStep, run.Tunnel, good: 80, fair: 180, "Net_Tunnel");
        Rows.Add(ProbeRow.From(Localizer.Get("Net_RowTunnel"), run.Tunnel));
    }

    /// <summary>The Bedrock tunnel, on a crossplay server: a UDP ping to the public address.</summary>
    private async Task TestBedrockTunnelAsync(RunState run, CancellationToken ct)
    {
        if (!Steps.Contains(BedrockStep)) return;

        var host = _server.BedrockHost;
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(_server.BedrockPortText, out var bedrockPort))
        {
            BedrockStep.Set(StepState.Skipped, Localizer.Get("Net_NoBedrock"), Localizer.Get("Net_NoBedrockDetail"));
            return;
        }
        if (!run.ServerRunning)
        {
            BedrockStep.Set(StepState.Skipped, Localizer.Get("Net_StepBedrock"), Localizer.Get("Net_NeedsServer"));
            return;
        }

        BedrockStep.Set(StepState.Running, Localizer.Get("Net_StepBedrock"), Localizer.Get("Net_Measuring"));
        run.Addresses.Add($"{Localizer.Get("Net_RowBedrock")}: {host}:{bedrockPort}");

        // A name that does not resolve is a tunnel nobody can reach: every ping lost.
        var bedrock = await ConnectionProbe.ResolveAsync(host, ct) is { } ip
            ? await ConnectionProbe.SampleAsync(
                c => ConnectionProbe.PingBedrockAsync(new IPEndPoint(ip, bedrockPort), Wait, c),
                Samples, Gap, Live(BedrockStep), ct)
            : new LatencyStats(Array.Empty<double>(), Samples);
        Judge(BedrockStep, bedrock, good: 80, fair: 180, "Net_Bedrock");
        Rows.Add(ProbeRow.From(Localizer.Get("Net_RowBedrock"), bedrock));
    }

    /// <summary>The chart of round trips, and the one line saying what the server is.</summary>
    private void ShowSamples(RunState run)
    {
        var series = run.Tunnel.AnyReply ? run.Tunnel : run.Local;
        SamplesSeries = series.Samples.ToList();
        SamplesTitle = Localizer.Format(run.Tunnel.AnyReply ? "Net_SamplesTunnelFmt"
            : "Net_SamplesLocalFmt",
            series.Received);

        if (run.Status is not null)
            // Not its protocol number: some servers report back whatever the ping asked with.
            run.Addresses.Add(Localizer.Format("Net_ServerInfoFmt", run.Status.Version,
                run.Status.Online, run.Status.Max));
        AddressesText = string.Join("   ·   ", run.Addresses);
    }

    /// <summary>Stops a test in progress: the page is going away.</summary>
    internal void Cancel() => _cts?.Cancel();

    /// <summary>Stops a test in progress and lets go of its token, for a server that is being removed.</summary>
    public void Dispose()
    {
        Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Cancels what was running and hands out the token for what runs next.</summary>
    private CancellationToken Renew()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

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
        var state = stats.Average < good ? StepState.Good : stats.Average < fair ? StepState.Fair
            : StepState.Bad;
        if (stats.Loss > 0.25) return StepState.Bad;
        if (stats.Loss > 0 && state == StepState.Good) return StepState.Fair;
        return state;
    }

    private static void Judge(ConnectionStepViewModel step, LatencyStats stats, double good, double fair,
        string prefix)
    {
        var state = Grade(stats, good, fair);
        var key = !stats.AnyReply ? "NoReply" : state.ToString();
        step.Set(state, Localizer.Get($"{prefix}{key}"), Localizer.Get($"{prefix}{key}Detail"),
            stats.AnyReply ? Ms(stats.Average) : Localizer.Get("Net_NoAnswer"));
    }

    internal void JudgeInternet(LatencyStats latency, Throughput up)
    {
        if (!latency.AnyReply)
        {
            InternetStep.Set(StepState.Bad, Localizer.Get("Net_InternetNone"),
                Localizer.Get("Net_InternetNoneDetail"),
                Localizer.Get("Net_NoAnswer"));
            return;
        }

        var byLatency = Grade(latency, good: 50, fair: 120);
        var byUpload = up.Mbps <= 0 ? StepState.Fair : up.Mbps < 3 ? StepState.Bad : up.Mbps < 10
            ? StepState.Fair : StepState.Good;
        var state = (StepState)Math.Max((int)byLatency, (int)byUpload);
        // About one megabit a player, which is generous: past a hundred the number stops meaning much.
        var players = Math.Clamp((int)Math.Floor(up.Mbps), 1, 100);

        var (title, detail) = state switch
        {
            StepState.Good => ("Net_InternetGood",
                Localizer.Format("Net_InternetGoodDetailFmt", players)),
            // Nothing went up: the line was not measured, which is not the same as lag.
            StepState.Fair when up.Mbps <= 0 && byLatency == StepState.Good =>
                ("Net_InternetNoSpeed", Localizer.Get("Net_InternetNoSpeedDetail")),
            StepState.Fair when byUpload >= byLatency && up.Mbps > 0 =>
                ("Net_InternetFair", Localizer.Format("Net_InternetSlowUpFmt", players)),
            StepState.Fair => ("Net_InternetFair", Localizer.Get("Net_InternetLaggyDetail")),
            _ => ("Net_InternetBad", Localizer.Get("Net_InternetBadDetail")),
        };
        InternetStep.Set(state, Localizer.Get(title), detail,
            up.Mbps > 0 ? "↑ " + Mbps(up) + " · " + Ms(latency.Average) : Ms(latency.Average));
    }

    private void Conclude()
    {
        var measured = Steps.Where(s => s.State is StepState.Good or StepState.Fair or StepState.Bad)
            .ToList();
        var worst = measured.OrderByDescending(s => (int)s.State).FirstOrDefault();
        if (worst is null || worst.State == StepState.Good)
        {
            VerdictState = StepState.Good;
            Verdict = Localizer.Get(ServerWasOff ? "Net_VerdictPartial" : "Net_VerdictGood");
            return;
        }
        VerdictState = worst.State;
        Verdict = Localizer.Format("Net_VerdictFmt", worst.Title, worst.Detail);
    }

    // ---------------------------------------------------------------- report

    /// <remarks>
    /// In the app's language, as the page is: it is pasted to whoever helps, and it used to mix the
    /// page's sentences with enum names ("[Fair]") and English headers.
    /// </remarks>
    internal void WriteReport(List<string> addresses, Throughput down, Throughput up)
    {
        foreach (var step in Steps)
            _report.AppendLine($"[{StateName(step.State)}] {step.Title} — {step.Badge} — {step.Detail}");
        _report.AppendLine();
        _report.AppendLine($"{Localizer.Get("Net_ColStretch")}: {Localizer.Get("Net_ColMin")} / " +
                           $"{Localizer.Get("Net_ColAvg")} / {Localizer.Get("Net_ColMax")} / " +
                           $"{Localizer.Get("Net_ColJitter")} (ms) / {Localizer.Get("Net_ColLoss")}");
        foreach (var row in Rows)
            _report.AppendLine($"{row.Name}: {row.Min} / {row.Average} / {row.Max} / {row.Jitter} / {row.Loss}");
        _report.AppendLine($"{Localizer.Get("Net_Download")}: {Mbps(down)} · " +
                           $"{Localizer.Get("Net_Upload")}: {Mbps(up)} · " +
                           $"{Localizer.Get("Net_Peak")}: {PeakText}");
        foreach (var a in addresses) _report.AppendLine(a);
        _report.AppendLine(Verdict);
    }

    /// <summary>What Copy puts on the clipboard.</summary>
    internal string Report => _report.ToString();

    private bool CanCopy => !IsTesting && _report.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyReport()
    {
        if (AppWindows.Main?.Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(Report);
            CopyText = Localizer.Get("Net_Copied");
        }
        catch { /* clipboard busy */ }
    }

    // ---------------------------------------------------------------- formats

    /// <summary>A step's state as the report names it; one never reached was not tested.</summary>
    private static string StateName(StepState state) => Localizer.Get(state switch
    {
        StepState.Good => "Net_StateGood",
        StepState.Fair => "Net_StateFair",
        StepState.Bad => "Net_StateBad",
        _ => "Net_StateSkipped",
    });

    private static string Ms(double ms) => ProbeRow.Number(ms) + " ms";

    private static string Mbps(Throughput t) =>
        t.Mbps <= 0 ? "—" : t.Mbps.ToString(t.Mbps < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " Mb/s";

    /// <summary>The upload peak, in decimal units like the Mb/s beside it: 8 Mb/s is 1 MB/s.</summary>
    private static string Bytes(double bytes) =>
        bytes >= 1_000_000 ? (bytes / 1_000_000).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
        : (bytes / 1_000).ToString("0", CultureInfo.CurrentCulture) + " kB";
}
