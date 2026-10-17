using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>Why a change to somebody's access is refused. <see cref="None"/> means it is allowed.</summary>
public enum GrantDenial
{
    /// <summary>Allowed.</summary>
    None,

    /// <summary>The person asking cannot manage members at all.</summary>
    NotAllowedToManageMembers,

    /// <summary>The target is the owner, whose access only a transfer can change.</summary>
    TargetIsOwner,

    /// <summary>The change would make somebody an owner. That is a transfer, not a role.</summary>
    CannotAssignOwner,

    /// <summary>The target can do things the person asking cannot, so they may not touch it.</summary>
    TargetOutranksActor,

    /// <summary>Only the owner hands out the critical permissions.</summary>
    CriticalNeedsOwner,

    /// <summary>The change would give the target something the person asking does not have.</summary>
    WouldEscalate
}

/// <summary>
/// Who can do what on a shared server, and who may change whose access.
/// </summary>
/// <remarks>
/// <para>
/// Pure functions of the data in <see cref="ServerGrant"/>, with no network and no screen. The app
/// uses them to decide what to show; the cloud has to reach the <em>same</em> answers on its own,
/// because a button that is hidden stops nobody who talks to the API directly. That is why
/// <c>ServerAccessTests</c> reads like a specification.
/// </para>
/// <para>
/// Every rule below compares permission sets instead of roles. A role is only a starting point, and
/// "an admin who was also given free commands" is a different person from "an admin" without a
/// second code path to say so.
/// </para>
/// </remarks>
public static class ServerAccessRules
{
    /// <summary>What a grant really lets its holder do.</summary>
    /// <remarks>
    /// <para>The role's preset, plus <c>Added</c>, minus <c>Removed</c> — so removing wins.</para>
    /// <para>
    /// Two things are not up to the overrides. The owner has everything and nothing can take it away,
    /// so a stored override on an owner is ignored. Everybody keeps <see cref="ServerPermission.ViewStatus"/>:
    /// a member who cannot even see the server is simply not a member, and should be removed
    /// instead. <see cref="ServerPermission.OwnerActions"/> never comes from an override.
    /// </para>
    /// </remarks>
    public static ServerPermission Effective(ServerGrant grant)
    {
        if (grant.Role == ServerRole.Owner) return ServerAccessCatalog.All;

        var set = (ServerAccessCatalog.Preset(grant.Role) | grant.Added) & ~grant.Removed;
        set &= ~ServerPermission.OwnerActions;
        return set | ServerPermission.ViewStatus;
    }

    /// <summary>Whether a grant includes every permission in <paramref name="permission"/>.</summary>
    public static bool Has(ServerGrant grant, ServerPermission permission) =>
        (Effective(grant) & permission) == permission;

    /// <summary>
    /// Whether <paramref name="actor"/> may give somebody the access in <paramref name="proposed"/>.
    /// </summary>
    /// <param name="actor">The person making the change.</param>
    /// <param name="existing">The target's current grant, or null when inviting a new member.</param>
    /// <param name="proposed">What the target would hold afterwards.</param>
    /// <remarks>
    /// <para>The checks run in this order, so the answer is the first reason that applies:</para>
    /// <list type="number">
    /// <item>Only someone with <see cref="ServerPermission.ManageMembers"/> changes access.</item>
    /// <item>The owner's access is not changed here, and nobody is made owner here.</item>
    /// <item>The target must not be able to do anything the actor cannot — nobody edits their betters.
    ///   Equals are allowed.</item>
    /// <item>A critical permission the target does not already have needs the owner to give it.</item>
    /// <item>Nobody can hand out more than they hold.</item>
    /// </list>
    /// </remarks>
    public static GrantDenial Check(ServerGrant actor, ServerGrant? existing, ServerGrant proposed)
    {
        if (!Has(actor, ServerPermission.ManageMembers)) return GrantDenial.NotAllowedToManageMembers;
        if (existing?.Role == ServerRole.Owner) return GrantDenial.TargetIsOwner;
        if (proposed.Role == ServerRole.Owner) return GrantDenial.CannotAssignOwner;

        var actorHas = Effective(actor);
        var before = existing == null ? ServerPermission.None : Effective(existing);
        var after = Effective(proposed);

        if ((before & ~actorHas) != 0) return GrantDenial.TargetOutranksActor;

        var newCritical = after & ServerAccessCatalog.Critical & ~before;
        if (newCritical != 0 && actor.Role != ServerRole.Owner) return GrantDenial.CriticalNeedsOwner;

        if ((after & ~actorHas) != 0) return GrantDenial.WouldEscalate;

        return GrantDenial.None;
    }

    /// <summary>Whether <paramref name="actor"/> may hand the server over to somebody else.</summary>
    public static bool CanTransferOwnership(ServerGrant actor) => actor.Role == ServerRole.Owner;

    /// <summary>What the previous owner is afterwards: an admin, so they are not locked out of their own server.</summary>
    public static readonly ServerGrant FormerOwnerGrant = new(ServerRole.Admin);

    /// <summary>The new owner's grant. Always a bare owner: nothing from before carries over.</summary>
    public static readonly ServerGrant NewOwnerGrant = new(ServerRole.Owner);
}
