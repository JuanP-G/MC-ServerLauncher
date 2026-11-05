using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>How a line of the account card should read: grey for "nothing to say", then green, amber, red.</summary>
public enum AccountLevel { Idle, Ok, Warn, Bad }

/// <summary>What a button on a way in does.</summary>
public enum LaneAction { Connect, ChangeCode, Retry, Disconnect, StartService, StopService }

/// <summary>
/// One way into the Playit account, as the card shows it: what it is, what state it is in, and what
/// can be done about it.
/// </summary>
/// <param name="Name">What the way is called.</param>
/// <param name="How">A few words on how it is connected.</param>
/// <param name="Level">The colour of its dot.</param>
/// <param name="Text">Its state, in a sentence.</param>
/// <param name="Actions">The buttons it offers, in order.</param>
/// <param name="Absent">True for a way this machine does not have, shown dimmed so the person can see
/// the other way exists.</param>
/// <param name="Accent">True when the first button is the one to press: nothing is connected yet.</param>
public sealed record AccountLane(string Name, string How, AccountLevel Level, bool Absent, string Text,
    IReadOnlyList<LaneAction> Actions, bool Accent = false);

/// <summary>The card's headline and its ways in.</summary>
public sealed record AccountCard(string Headline, AccountLevel Level, IReadOnlyList<AccountLane> Lanes);

/// <summary>
/// What the Playit account card says, worked out from facts about this machine and the last reading.
/// </summary>
/// <remarks>
/// <para>
/// The card has to answer two questions at a glance: is the account connected, and through which way.
/// There are two ways a person can have set it up — the agent this app downloads and connects with a
/// setup code, and the Playit they installed on the PC themselves — and they can have both. A third,
/// a key saved by an old version, only shows when it exists.
/// </para>
/// <para>
/// "Connected" and "active" are told apart on purpose: Playit can be installed, with its key on disk,
/// and switched off. The account can then be read, but no tunnel carries traffic, and a green
/// "connected" would hide exactly the thing the person is looking for.
/// </para>
/// A pure function of its input, so each case can be checked without a window, an agent or a network.
/// </remarks>
public static class PlayitAccountStatus
{
    /// <summary>The facts the card is made from.</summary>
    /// <param name="HasAppCode">The connect flow saved an agent key: the app's own agent is in play.</param>
    /// <param name="Agent">State of the app's agent.</param>
    /// <param name="AgentError">Why the agent failed, if it did.</param>
    /// <param name="AppRefused">Playit rejected the app's key.</param>
    /// <param name="HasInstalledKey">The installed Playit's key was found on this PC.</param>
    /// <param name="ServiceInstalled">Playit's system service (or binary) is installed.</param>
    /// <param name="ServiceState">State of that service.</param>
    /// <param name="InstalledRefused">Playit rejected the installed agent's key.</param>
    /// <param name="InstalledReadOnly">That key can read tunnels but not change them.</param>
    /// <param name="HasSavedKey">A key saved by an older version is in use.</param>
    /// <param name="SavedRefused">Playit rejected that key.</param>
    /// <param name="Checking">There is a way in, and Playit has not answered yet.</param>
    public sealed record Input(
        bool HasAppCode = false, AgentRunState Agent = AgentRunState.Stopped, string? AgentError = null,
        bool AppRefused = false,
        bool HasInstalledKey = false, bool ServiceInstalled = false,
        PlayitState ServiceState = PlayitState.Stopped, bool InstalledRefused = false,
        bool InstalledReadOnly = false,
        bool HasSavedKey = false, bool SavedRefused = false,
        bool Checking = false);

    public static AccountCard Build(Input i)
    {
        var anyWay = i.HasAppCode || i.HasInstalledKey || i.HasSavedKey;
        var lanes = new List<AccountLane> { AppLane(i, anyWay), InstalledLane(i) };
        if (i.HasSavedKey) lanes.Add(SavedLane(i));

        var (headline, level) = Headline(i, anyWay);
        return new AccountCard(headline, level, lanes);
    }

    private static (string, AccountLevel) Headline(Input i, bool anyWay)
    {
        if (!anyWay) return (Localizer.Get("Pa_Head_None"), AccountLevel.Idle);
        if (i.Checking) return (Localizer.Get("Pa_Head_Checking"), AccountLevel.Idle);

        var appWorks = i.HasAppCode && !i.AppRefused;
        var installedWorks = i.HasInstalledKey && !i.InstalledRefused;
        var savedWorks = i.HasSavedKey && !i.SavedRefused;
        if (!appWorks && !installedWorks && !savedWorks)
            return (Localizer.Get("Pa_Head_Refused"), AccountLevel.Bad);

        // Something carries traffic: the app's agent, or the installed service. Reading the account
        // with a key says nothing about that.
        if ((appWorks && i.Agent == AgentRunState.Running)
            || (installedWorks && i.ServiceState == PlayitState.Running))
            return (Localizer.Get("Pa_Head_Active"), AccountLevel.Ok);

        if ((appWorks && i.Agent is AgentRunState.Downloading or AgentRunState.Starting)
            || (installedWorks && i.ServiceState == PlayitState.Starting))
            return (Localizer.Get("Pa_Head_Starting"), AccountLevel.Warn);

        return (Localizer.Get("Pa_Head_Inactive"), AccountLevel.Warn);
    }

    private static AccountLane AppLane(Input i, bool anyWay)
    {
        var name = Localizer.Get("Pa_App_Name");
        var how = Localizer.Get("Pa_App_How");

        if (!i.HasAppCode)
            return new AccountLane(name, how, AccountLevel.Idle, Absent: true, Localizer.Get("Pa_App_None"),
                [LaneAction.Connect], Accent: !anyWay);

        var (level, text) = i.AppRefused
            ? (AccountLevel.Bad, Localizer.Get("Pa_App_Refused"))
            : i.Agent switch
            {
                AgentRunState.Running => (AccountLevel.Ok, Localizer.Get("Pa_App_Running")),
                AgentRunState.Downloading => (AccountLevel.Warn, Localizer.Get("Pk_Agent_Downloading")),
                AgentRunState.Starting => (AccountLevel.Warn, Localizer.Get("Pk_Agent_Starting")),
                AgentRunState.Failed => (AccountLevel.Bad,
                    Localizer.Format("Pk_Agent_Failed", i.AgentError ?? "")),
                AgentRunState.Unsupported => (AccountLevel.Idle, Localizer.Get("Pk_Agent_Unsupported")),
                _ => (AccountLevel.Warn, Localizer.Get("Pa_App_Stopped")),
            };

        var retry = !i.AppRefused && i.Agent is AgentRunState.Failed or AgentRunState.Stopped;
        List<LaneAction> actions = retry
            ? [LaneAction.Retry, LaneAction.ChangeCode, LaneAction.Disconnect]
            : [LaneAction.ChangeCode, LaneAction.Disconnect];
        return new AccountLane(name, how, level, Absent: false, text, actions);
    }

    private static AccountLane InstalledLane(Input i)
    {
        var name = Localizer.Get("Pa_Inst_Name");
        var how = Localizer.Get("Pa_Inst_How");

        if (!i.HasInstalledKey)
            return new AccountLane(name, how, AccountLevel.Idle, Absent: true, Localizer.Get("Pa_Inst_None"), []);

        var (level, text) =
            i.InstalledRefused ? (AccountLevel.Bad, Localizer.Get("Pa_Inst_Refused"))
            : i.ServiceState == PlayitState.Running ? (AccountLevel.Ok, Localizer.Get("Pa_Inst_On"))
            : i.ServiceState == PlayitState.Starting ? (AccountLevel.Warn, Localizer.Get("Pa_Inst_Starting"))
            : i.ServiceInstalled ? (AccountLevel.Warn, Localizer.Get("Pa_Inst_Off"))
            : (AccountLevel.Warn, Localizer.Get("Pa_Inst_NoService"));

        if (i.InstalledReadOnly && !i.InstalledRefused)
            text += " " + Localizer.Get("Pa_Inst_ReadOnly");

        // Switching the service on and off is only offered where there is one to switch.
        List<LaneAction> actions = !i.ServiceInstalled ? []
            : i.ServiceState == PlayitState.Running ? [LaneAction.StopService]
            : [LaneAction.StartService];
        return new AccountLane(name, how, level, Absent: false, text, actions);
    }

    private static AccountLane SavedLane(Input i) =>
        new(Localizer.Get("Pa_Saved_Name"), Localizer.Get("Pa_Saved_How"),
            i.SavedRefused ? AccountLevel.Bad : AccountLevel.Ok, Absent: false,
            Localizer.Get(i.SavedRefused ? "Pa_Saved_Refused" : "Pa_Saved_State"),
            // The app's own button already forgets everything; two of them would say the same thing twice.
            i.HasAppCode ? [] : [LaneAction.Disconnect]);
}
