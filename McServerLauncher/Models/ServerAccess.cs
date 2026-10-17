namespace McServerLauncher.Models;

/// <summary>One thing a person may be allowed to do on a shared server.</summary>
/// <remarks>
/// <para>
/// A set of these is a bit mask, so a role, an override and a person's effective rights are all the
/// same type and combining them is a plain <c>|</c> and <c>&amp;</c>. The numbers are an in-memory
/// detail: anything that leaves the process (the cloud, a file) uses the stable ids in
/// <see cref="Services.ServerAccessCatalog"/>, so a member can be added on the end without
/// renumbering anything.
/// </para>
/// <para>
/// There is one entry per decision somebody might want to take away from somebody else, not one per
/// button. Two of them — <see cref="RunCommands"/> and <see cref="ManageContent"/> — are
/// <see cref="Services.PermissionRisk.Critical"/>: both end, one way or another, in code running on
/// the host PC.
/// </para>
/// </remarks>
[Flags]
public enum ServerPermission : long
{
    None = 0,

    /// <summary>See whether the server is up, its address and who is connected.</summary>
    ViewStatus = 1L << 0,

    /// <summary>Read the console.</summary>
    ViewConsole = 1L << 1,

    /// <summary>Start, stop and restart.</summary>
    StartStop = 1L << 2,

    /// <summary>Whitelist, bans and operators.</summary>
    ManagePlayers = 1L << 3,

    /// <summary>Create and restore world backups.</summary>
    ManageBackups = 1L << 4,

    /// <summary>Change the server's configuration.</summary>
    EditSettings = 1L << 5,

    /// <summary>Send free-form console commands.</summary>
    RunCommands = 1L << 6,

    /// <summary>Install and remove mods and plugins — arbitrary code on the host.</summary>
    ManageContent = 1L << 7,

    /// <summary>Invite people and change their role or overrides.</summary>
    ManageMembers = 1L << 8,

    /// <summary>Transfer the server to somebody else, or delete it. Only the owner ever has this.</summary>
    OwnerActions = 1L << 9
}

/// <summary>A ready-made set of permissions that a member starts from.</summary>
/// <remarks>
/// Not a ladder. A <see cref="Moderator"/> can do things an <see cref="Operator"/> cannot and the
/// other way round, which is why the rules compare permission sets rather than ranks.
/// </remarks>
public enum ServerRole
{
    /// <summary>Looks, touches nothing.</summary>
    Observer,

    /// <summary>Looks at the console and starts, stops and restarts.</summary>
    Operator,

    /// <summary>Looks at the console and manages players, but cannot stop the server.</summary>
    Moderator,

    /// <summary>Everything except the critical permissions and the owner's own actions.</summary>
    Admin,

    /// <summary>Everything, and the only one who can hand out the critical permissions.</summary>
    Owner
}

/// <summary>What a member was given: a role plus the changes made to it for this one person.</summary>
/// <remarks>
/// This is what gets stored and sent. What the person can actually do is computed from it by
/// <see cref="Services.ServerAccessRules.Effective"/> and never stored, so changing what a role means
/// takes effect on everybody without touching a single record.
/// </remarks>
/// <param name="Role">The role they started from.</param>
/// <param name="Added">Permissions switched on beyond the role.</param>
/// <param name="Removed">Permissions switched off from the role. Wins over <paramref name="Added"/>.</param>
public sealed record ServerGrant(
    ServerRole Role,
    ServerPermission Added = ServerPermission.None,
    ServerPermission Removed = ServerPermission.None);
