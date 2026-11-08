using System.Windows.Input;
using Avalonia.Media;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// One way into the Playit account on the tunnels screen: its name, its state and its buttons.
/// </summary>
/// <remarks>
/// A thin shell over <see cref="AccountLane"/>. The decisions are made there, where they can be tested;
/// this only turns them into brushes, visibility flags and the commands of the screen.
/// </remarks>
public sealed class TunnelLaneViewModel
{
    private readonly AccountLane _lane;

    public TunnelLaneViewModel(AccountLane lane, IBrush brush, TunnelsViewModel screen)
    {
        _lane = lane;
        Brush = brush;
        ConnectCommand = screen.ConnectCommand;
        RetryCommand = screen.RetryAgentCommand;
        DisconnectCommand = screen.DisconnectCommand;
        ServiceCommand = screen.ToggleServiceCommand;
    }

    public string Name => _lane.Name;
    public string How => _lane.How;
    public string Text => _lane.Text;
    public IBrush Brush { get; }

    /// <summary>A way this machine does not have: drawn dimmed, with an empty dot.</summary>
    public bool IsAbsent => _lane.Absent;
    public bool IsPresent => !_lane.Absent;
    public double Dim => _lane.Absent ? 0.65 : 1;

    public ICommand ConnectCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ServiceCommand { get; }

    private bool Offers(LaneAction action) => _lane.Actions.Contains(action);

    // The first button is the one to press only when nothing is connected at all.
    public bool ShowConnectAccent => _lane.Accent && Offers(LaneAction.Connect);
    public bool ShowConnectPlain => !_lane.Accent && Offers(LaneAction.Connect);
    public string ConnectText => Localizer.Get(Offers(LaneAction.Connect) ? "Pa_Act_Connect" : "Pa_Act_ChangeCode");
    public bool ShowChangeCode => Offers(LaneAction.ChangeCode);
    public bool ShowRetry => Offers(LaneAction.Retry);
    public bool ShowDisconnect => Offers(LaneAction.Disconnect);
    public bool ShowService => Offers(LaneAction.StartService) || Offers(LaneAction.StopService);
    public string ServiceText => Localizer.Get(Offers(LaneAction.StopService) ? "Pa_Act_StopService" : "Pa_Act_StartService");
}
