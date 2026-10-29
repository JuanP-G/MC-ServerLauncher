using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What the tunnel panel says about Playit, worked out from the two things that can carry the
/// tunnel: the app's embedded agent, and the system's own Playit service.
/// </summary>
/// <remarks>
/// The embedded agent wins whenever the user has connected their account — that is the model this
/// app uses, and the system service is then irrelevant and must not say "not installed". Pure
/// functions of their inputs, so the mapping can be checked without a running agent.
/// </remarks>
public static class PlayitStatus
{
    /// <summary>
    /// The tunnel data-plane state as the UI should see it: the embedded agent when the account is
    /// connected, otherwise the legacy system service.
    /// </summary>
    public static PlayitState Effective(bool agentHasSecret, AgentRunState agent, PlayitState system) =>
        agentHasSecret
            ? agent switch
            {
                AgentRunState.Running => PlayitState.Running,
                AgentRunState.Downloading or AgentRunState.Starting => PlayitState.Starting,
                _ => PlayitState.Stopped
            }
            : system;

    /// <summary>The line under the tunnel row, saying what is actually forwarding traffic.</summary>
    /// <param name="agentHasSecret">Whether the embedded agent is the one in play.</param>
    /// <param name="agent">The embedded agent's state.</param>
    /// <param name="state">The effective state, from <see cref="Effective"/>.</param>
    /// <param name="systemInstalled">Whether the system service is installed.</param>
    public static string Text(bool agentHasSecret, AgentRunState agent, PlayitState state, bool systemInstalled)
    {
        // Embedded-agent model: report what the agent is actually doing.
        if (agentHasSecret)
        {
            return agent switch
            {
                AgentRunState.Running => Localizer.Get("Playit_Active"),
                AgentRunState.Downloading => Localizer.Get("Pk_Agent_Downloading"),
                AgentRunState.Starting => Localizer.Get("Status_Starting"),
                AgentRunState.Failed => Localizer.Get("Playit_AgentFailed"),
                AgentRunState.Unsupported => Localizer.Get("Pk_Agent_Unsupported"),
                _ => Localizer.Get("Playit_AgentStopped")
            };
        }

        return state switch
        {
            PlayitState.Running => Localizer.Get(systemInstalled ? "Playit_ActiveBg" : "Playit_Active"),
            PlayitState.Starting => Localizer.Get("Status_Starting"),
            _ => Localizer.Get(systemInstalled ? "Playit_Stopped" : "Playit_NotInstalled")
        };
    }
}
