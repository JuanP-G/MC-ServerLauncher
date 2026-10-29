using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>How reachable a server is, for the coloured dot beside its name.</summary>
public enum SignalKind
{
    /// <summary>The server is not running.</summary>
    Off,
    /// <summary>The server is starting or stopping.</summary>
    Transition,
    /// <summary>It runs but is meant to go through a tunnel that is not up.</summary>
    NoTunnel,
    /// <summary>It runs and players can reach it.</summary>
    Accessible,
}

/// <summary>What the signal shows: its kind, the hint to explain it, and whether to warn.</summary>
/// <param name="Kind">The kind of signal; the view model picks its colour.</param>
/// <param name="HintKey">The resource key of the hint under the dot.</param>
/// <param name="ShowTunnelWarning">Whether to draw attention to the missing tunnel.</param>
public readonly record struct ReachabilitySignal(SignalKind Kind, string HintKey, bool ShowTunnelWarning)
{
    /// <summary>
    /// Computes the "signal" (real reachability): green only if the server is running and, when
    /// using Playit, the tunnel is active. If running without a tunnel, red + warning.
    /// </summary>
    public static ReachabilitySignal For(ServerState state, bool playitEnabled, PlayitState playit)
    {
        if (state != ServerState.Running)
        {
            var transitioning = state is ServerState.Starting or ServerState.Stopping;
            return transitioning
                ? new(SignalKind.Transition, "Signal_Transition", false)
                : new(SignalKind.Off, "Signal_Off", false);
        }

        return playitEnabled && playit != PlayitState.Running
            ? new(SignalKind.NoTunnel, "Signal_NoTunnel", true)
            : new(SignalKind.Accessible, "Signal_Accessible", false);
    }
}
