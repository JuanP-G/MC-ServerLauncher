using System.Collections.Generic;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>How much damage a permission can do if it falls into the wrong hands.</summary>
public enum PermissionRisk
{
    /// <summary>Anything a normal member can reasonably be given.</summary>
    Normal,

    /// <summary>
    /// Ends in code running on the host PC, so it is never part of a preset and only the owner can
    /// hand it out.
    /// </summary>
    Critical
}

/// <summary>
/// One row per permission and per role: what it is called on the wire, how dangerous it is, and
/// what a role starts with.
/// </summary>
/// <remarks>
/// <para>
/// Same instinct as <see cref="ServerTypeCatalog"/>: a single table that everything reads from. The
/// cloud, the web and the phone are going to ask the same questions, and they must not each keep their
/// own list.
/// </para>
/// <para>
/// This has no text a person reads. Names and descriptions come with the screen that shows them, in
/// the .resx files like everything else; the ids below are for machines and never change.
/// </para>
/// </remarks>
public static class ServerAccessCatalog
{
    /// <summary>Everything the app knows about one permission.</summary>
    /// <param name="Permission">The single bit.</param>
    /// <param name="Id">What travels on the wire and into files. Never renamed.</param>
    /// <param name="Risk">Whether it is part of the owner-only tier.</param>
    public record PermissionEntry(ServerPermission Permission, string Id, PermissionRisk Risk);

    /// <summary>The permissions, in the order a screen should list them.</summary>
    public static readonly PermissionEntry[] Permissions =
    {
        new(ServerPermission.ViewStatus, "view.status", PermissionRisk.Normal),
        new(ServerPermission.ViewConsole, "view.console", PermissionRisk.Normal),
        new(ServerPermission.StartStop, "server.control", PermissionRisk.Normal),
        new(ServerPermission.ManagePlayers, "players.manage", PermissionRisk.Normal),
        new(ServerPermission.ManageBackups, "backups.manage", PermissionRisk.Normal),
        new(ServerPermission.EditSettings, "settings.edit", PermissionRisk.Normal),
        new(ServerPermission.RunCommands, "console.commands", PermissionRisk.Critical),
        new(ServerPermission.ManageContent, "content.manage", PermissionRisk.Critical),
        new(ServerPermission.ManageMembers, "members.manage", PermissionRisk.Normal),
        new(ServerPermission.OwnerActions, "server.owner", PermissionRisk.Normal),
    };

    /// <summary>Every permission there is.</summary>
    public static readonly ServerPermission All = Permissions.Aggregate(ServerPermission.None, (a, e) => a | e.Permission);

    /// <summary>The critical permissions together: free commands and installing content.</summary>
    public static readonly ServerPermission Critical = Permissions
        .Where(e => e.Risk == PermissionRisk.Critical)
        .Aggregate(ServerPermission.None, (a, e) => a | e.Permission);

    /// <summary>
    /// What a role starts with.
    /// </summary>
    /// <remarks>
    /// The critical permissions are in none of these but the owner's. An admin who needs one gets it
    /// as an override from the owner, which leaves a record of who decided that.
    /// </remarks>
    public static ServerPermission Preset(ServerRole role) => role switch
    {
        ServerRole.Observer => ServerPermission.ViewStatus,
        ServerRole.Operator => ServerPermission.ViewStatus | ServerPermission.ViewConsole | ServerPermission.StartStop,
        ServerRole.Moderator => ServerPermission.ViewStatus | ServerPermission.ViewConsole | ServerPermission.ManagePlayers,
        ServerRole.Admin => ServerPermission.ViewStatus | ServerPermission.ViewConsole | ServerPermission.StartStop
            | ServerPermission.ManagePlayers | ServerPermission.ManageBackups | ServerPermission.EditSettings
            | ServerPermission.ManageMembers,
        ServerRole.Owner => All,
        _ => ServerPermission.None
    };

    /// <summary>The roles, from the one that can do least to the one that can do most.</summary>
    public static readonly ServerRole[] Roles =
    {
        ServerRole.Observer, ServerRole.Operator, ServerRole.Moderator, ServerRole.Admin, ServerRole.Owner
    };

    /// <summary>The wire id of a role: its name in lower case.</summary>
    public static string RoleId(ServerRole role) => role.ToString().ToLowerInvariant();

    /// <summary>The role with this id, or null for one this version has never heard of.</summary>
    public static ServerRole? ParseRole(string? id) =>
        Roles.Cast<ServerRole?>().FirstOrDefault(r => string.Equals(RoleId(r!.Value), id, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The ids of the permissions in a set, in catalog order.</summary>
    public static string[] ToIds(ServerPermission set) =>
        Permissions.Where(e => (set & e.Permission) == e.Permission).Select(e => e.Id).ToArray();

    /// <summary>
    /// The set named by these ids. An id this version does not know is skipped.
    /// </summary>
    /// <remarks>
    /// Skipped, not an error: a newer cloud may send a permission an older app has never heard of, and
    /// the safe reading of "something I don't understand" is that it grants nothing.
    /// </remarks>
    public static ServerPermission FromIds(IEnumerable<string>? ids)
    {
        var set = ServerPermission.None;
        if (ids == null) return set;
        foreach (var id in ids)
        {
            var entry = Permissions.FirstOrDefault(e => e.Id == id);
            if (entry != null) set |= entry.Permission;
        }
        return set;
    }
}
