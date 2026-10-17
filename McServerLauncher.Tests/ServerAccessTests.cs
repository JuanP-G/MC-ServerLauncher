using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Who can do what on a shared server, written as the specification the cloud has to meet too.
/// </summary>
/// <remarks>
/// The app only uses these rules to decide what to show. The cloud decides what is allowed, in its
/// own code, so these cases are the contract between the two: when the cloud exists, the same cases
/// run against it.
/// </remarks>
public class ServerAccessTests
{
    private const ServerPermission Status = ServerPermission.ViewStatus;
    private const ServerPermission Console = ServerPermission.ViewConsole;
    private const ServerPermission Control = ServerPermission.StartStop;
    private const ServerPermission Players = ServerPermission.ManagePlayers;

    private static readonly ServerGrant Owner = new(ServerRole.Owner);
    private static readonly ServerGrant Admin = new(ServerRole.Admin);
    private static readonly ServerGrant Op = new(ServerRole.Operator);
    private static readonly ServerGrant Mod = new(ServerRole.Moderator);
    private static readonly ServerGrant Watcher = new(ServerRole.Observer);

    // ---- what each role starts with -------------------------------------------------------

    [Theory]
    [InlineData(ServerRole.Observer, ServerPermission.ViewStatus)]
    [InlineData(ServerRole.Operator, ServerPermission.ViewStatus | ServerPermission.ViewConsole | ServerPermission.StartStop)]
    [InlineData(ServerRole.Moderator, ServerPermission.ViewStatus | ServerPermission.ViewConsole | ServerPermission.ManagePlayers)]
    public void ThePresetsAreWhatTheDesignSays(ServerRole role, ServerPermission expected)
    {
        Assert.Equal(expected, ServerAccessCatalog.Preset(role));
    }

    [Fact]
    public void AnAdminCanDoEverythingExceptTheCriticalAndTheOwnersOwn()
    {
        var everything = ServerAccessCatalog.All;
        var admin = ServerAccessRules.Effective(Admin);

        Assert.Equal(everything & ~ServerAccessCatalog.Critical & ~ServerPermission.OwnerActions, admin);
    }

    [Fact]
    public void OnlyTheOwnerStartsWithTheCriticalPermissions()
    {
        // Installing a mod or typing a command ends in code on the host PC. A preset that included
        // either would hand that to everyone who is given the role.
        foreach (var role in ServerAccessCatalog.Roles)
        {
            var hasCritical = (ServerAccessCatalog.Preset(role) & ServerAccessCatalog.Critical) != 0;
            Assert.Equal(role == ServerRole.Owner, hasCritical);
        }
    }

    [Fact]
    public void TheCriticalOnesAreExactlyCommandsAndContent()
    {
        Assert.Equal(ServerPermission.RunCommands | ServerPermission.ManageContent, ServerAccessCatalog.Critical);
    }

    // ---- presets plus the changes made for one person ---------------------------------------

    [Fact]
    public void AddingAPermissionGivesIt()
    {
        var grant = new ServerGrant(ServerRole.Observer, Added: Console);

        Assert.True(ServerAccessRules.Has(grant, Console));
        Assert.False(ServerAccessRules.Has(grant, Control));
    }

    [Fact]
    public void RemovingAPermissionTakesItAway()
    {
        var grant = new ServerGrant(ServerRole.Operator, Removed: Control);

        Assert.False(ServerAccessRules.Has(grant, Control));
        Assert.True(ServerAccessRules.Has(grant, Console));
    }

    [Fact]
    public void RemovingWinsWhenTheSameBitIsAlsoAdded()
    {
        var grant = new ServerGrant(ServerRole.Observer, Added: Control, Removed: Control);

        Assert.False(ServerAccessRules.Has(grant, Control));
    }

    [Fact]
    public void EverybodyKeepsSeeingTheServer()
    {
        // A member who cannot see the server is not a member; the way to say that is to remove them.
        var grant = new ServerGrant(ServerRole.Operator, Removed: Status);

        Assert.True(ServerAccessRules.Has(grant, Status));
    }

    [Fact]
    public void AnOverrideNeverGivesTheOwnersOwnActions()
    {
        var grant = new ServerGrant(ServerRole.Admin, Added: ServerPermission.OwnerActions);

        Assert.False(ServerAccessRules.Has(grant, ServerPermission.OwnerActions));
    }

    [Fact]
    public void TheOwnerHasEverythingWhateverIsStoredOnThem()
    {
        var odd = new ServerGrant(ServerRole.Owner, Removed: ServerAccessCatalog.All);

        Assert.Equal(ServerAccessCatalog.All, ServerAccessRules.Effective(odd));
    }

    // ---- who may change whose access ---------------------------------------------------------

    [Fact]
    public void SomeoneWithoutManageMembersChangesNothing()
    {
        Assert.Equal(GrantDenial.NotAllowedToManageMembers, ServerAccessRules.Check(Op, null, Watcher));
        Assert.Equal(GrantDenial.NotAllowedToManageMembers, ServerAccessRules.Check(Mod, Watcher, Op));
    }

    [Fact]
    public void AnAdminCanInviteAnOperator()
    {
        Assert.Equal(GrantDenial.None, ServerAccessRules.Check(Admin, null, Op));
    }

    [Fact]
    public void AnAdminCanDemoteAnOperator()
    {
        Assert.Equal(GrantDenial.None, ServerAccessRules.Check(Admin, Op, Watcher));
    }

    [Fact]
    public void NobodyIsMadeOwnerByChangingARole()
    {
        Assert.Equal(GrantDenial.CannotAssignOwner, ServerAccessRules.Check(Admin, Op, Owner));
        Assert.Equal(GrantDenial.CannotAssignOwner, ServerAccessRules.Check(Owner, Op, Owner));
    }

    [Fact]
    public void TheOwnersAccessIsNotChangedHere()
    {
        Assert.Equal(GrantDenial.TargetIsOwner, ServerAccessRules.Check(Admin, Owner, Op));
        Assert.Equal(GrantDenial.TargetIsOwner, ServerAccessRules.Check(Owner, Owner, Admin));
    }

    [Fact]
    public void NobodyEditsSomeoneWhoCanDoMoreThanTheyCan()
    {
        // A moderator who was given ManageMembers still cannot touch an admin: the admin can start
        // and stop the server and the moderator cannot.
        var moderatorWhoInvites = new ServerGrant(ServerRole.Moderator, Added: ServerPermission.ManageMembers);

        Assert.Equal(GrantDenial.TargetOutranksActor, ServerAccessRules.Check(moderatorWhoInvites, Admin, Op));
    }

    [Fact]
    public void TwoAdminsMayChangeEachOther()
    {
        // Equals are allowed. If that turns out to start fights, tighten this one rule and nothing else.
        Assert.Equal(GrantDenial.None, ServerAccessRules.Check(Admin, Admin, Op));
    }

    [Fact]
    public void NobodyHandsOutMoreThanTheyHold()
    {
        var moderatorWhoInvites = new ServerGrant(ServerRole.Moderator, Added: ServerPermission.ManageMembers);

        // Starting and stopping is something this moderator cannot do, so cannot give.
        Assert.Equal(GrantDenial.WouldEscalate, ServerAccessRules.Check(moderatorWhoInvites, null, Op));
        // Their own level is fine.
        Assert.Equal(GrantDenial.None, ServerAccessRules.Check(moderatorWhoInvites, null, Mod));
    }

    [Theory]
    [InlineData(ServerPermission.RunCommands)]
    [InlineData(ServerPermission.ManageContent)]
    public void AnAdminCannotGiveACriticalPermission(ServerPermission critical)
    {
        var withIt = new ServerGrant(ServerRole.Operator, Added: critical);

        Assert.Equal(GrantDenial.CriticalNeedsOwner, ServerAccessRules.Check(Admin, Op, withIt));
    }

    [Theory]
    [InlineData(ServerPermission.RunCommands)]
    [InlineData(ServerPermission.ManageContent)]
    public void TheOwnerCanGiveACriticalPermission(ServerPermission critical)
    {
        var admin = new ServerGrant(ServerRole.Admin, Added: critical);

        Assert.Equal(GrantDenial.None, ServerAccessRules.Check(Owner, Admin, admin));
    }

    [Fact]
    public void AnAdminWhoWasGivenAPermissionCannotPassItOn()
    {
        // The owner trusted this one person. That does not become permission to trust others.
        var trusted = new ServerGrant(ServerRole.Admin, Added: ServerPermission.RunCommands);
        var proposed = new ServerGrant(ServerRole.Operator, Added: ServerPermission.RunCommands);

        Assert.Equal(GrantDenial.CriticalNeedsOwner, ServerAccessRules.Check(trusted, Op, proposed));
    }

    [Fact]
    public void KeepingACriticalPermissionAlreadyHeldIsNotGivingIt()
    {
        // Demoting a trusted admin to an operator who still has the permission changes nothing about
        // that permission, so it does not need the owner. Everything else is still checked.
        var trusted = new ServerGrant(ServerRole.Admin, Added: ServerPermission.RunCommands);
        var keepsIt = new ServerGrant(ServerRole.Operator, Added: ServerPermission.RunCommands);
        var actor = new ServerGrant(ServerRole.Admin, Added: ServerPermission.RunCommands);

        Assert.Equal(GrantDenial.None, ServerAccessRules.Check(actor, trusted, keepsIt));
    }

    [Fact]
    public void AnAdminCannotEditSomeoneWhoWasGivenACriticalPermission()
    {
        var trusted = new ServerGrant(ServerRole.Operator, Added: ServerPermission.RunCommands);

        Assert.Equal(GrantDenial.TargetOutranksActor, ServerAccessRules.Check(Admin, trusted, Watcher));
    }

    [Fact]
    public void TheOwnerMayChangeAnyoneButThemselves()
    {
        foreach (var role in new[] { ServerRole.Observer, ServerRole.Operator, ServerRole.Moderator, ServerRole.Admin })
            Assert.Equal(GrantDenial.None, ServerAccessRules.Check(Owner, new ServerGrant(role), Op));
    }

    // ---- handing the server over ----------------------------------------------------------------

    [Fact]
    public void OnlyTheOwnerCanTransfer()
    {
        Assert.True(ServerAccessRules.CanTransferOwnership(Owner));
        Assert.False(ServerAccessRules.CanTransferOwnership(Admin));
        Assert.False(ServerAccessRules.CanTransferOwnership(new ServerGrant(ServerRole.Admin, Added: ServerAccessCatalog.All)));
    }

    [Fact]
    public void AfterATransferTheOldOwnerIsAnAdminAndTheNewOneIsBare()
    {
        Assert.Equal(ServerRole.Admin, ServerAccessRules.FormerOwnerGrant.Role);
        Assert.Equal(ServerPermission.None, ServerAccessRules.FormerOwnerGrant.Added);
        Assert.Equal(new ServerGrant(ServerRole.Owner), ServerAccessRules.NewOwnerGrant);
    }

    // ---- what goes on the wire -------------------------------------------------------------------

    [Fact]
    public void TheIdsAreTheWireFormatAndMustNotMove()
    {
        // These strings will be stored in the cloud and sent to every client. Renaming one silently
        // revokes (or, worse, fails to revoke) that permission for everybody already saved.
        var expected = new[]
        {
            (ServerPermission.ViewStatus, "view.status"),
            (ServerPermission.ViewConsole, "view.console"),
            (ServerPermission.StartStop, "server.control"),
            (ServerPermission.ManagePlayers, "players.manage"),
            (ServerPermission.ManageBackups, "backups.manage"),
            (ServerPermission.EditSettings, "settings.edit"),
            (ServerPermission.RunCommands, "console.commands"),
            (ServerPermission.ManageContent, "content.manage"),
            (ServerPermission.ManageMembers, "members.manage"),
            (ServerPermission.OwnerActions, "server.owner"),
        };

        Assert.Equal(expected.Length, ServerAccessCatalog.Permissions.Length);
        foreach (var (permission, id) in expected)
            Assert.Equal(id, ServerAccessCatalog.Permissions.Single(e => e.Permission == permission).Id);
    }

    [Fact]
    public void EveryPermissionHasExactlyOneRow()
    {
        // A permission added to the enum and forgotten in the table would exist, be checkable, and
        // be impossible to send. Same trap ServerConfigEffects closes for config fields.
        var members = Enum.GetValues<ServerPermission>().Where(p => p != ServerPermission.None).ToList();

        foreach (var member in members)
            Assert.Single(ServerAccessCatalog.Permissions, e => e.Permission == member);

        Assert.Equal(members.Count, ServerAccessCatalog.Permissions.Length);
        Assert.Equal(members.Count, ServerAccessCatalog.Permissions.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void ASetSurvivesBeingSentAndReceived()
    {
        var set = Status | Console | ServerPermission.ManageContent;

        Assert.Equal(set, ServerAccessCatalog.FromIds(ServerAccessCatalog.ToIds(set)));
    }

    [Fact]
    public void AnIdFromANewerVersionGrantsNothing()
    {
        var set = ServerAccessCatalog.FromIds(new[] { "view.status", "something.new", "" });

        Assert.Equal(Status, set);
        Assert.Equal(ServerPermission.None, ServerAccessCatalog.FromIds(null));
    }

    [Theory]
    [InlineData("observer", ServerRole.Observer)]
    [InlineData("OWNER", ServerRole.Owner)]
    [InlineData("Moderator", ServerRole.Moderator)]
    public void RolesAreReadByNameWhateverTheCase(string id, ServerRole expected)
    {
        Assert.Equal(expected, ServerAccessCatalog.ParseRole(id));
    }

    [Fact]
    public void ARoleThisVersionDoesNotKnowIsNotGuessed()
    {
        Assert.Null(ServerAccessCatalog.ParseRole("superadmin"));
        Assert.Null(ServerAccessCatalog.ParseRole(null));
    }

    [Fact]
    public void TheRoleIdsAreTheWireFormatAndMustNotMove()
    {
        Assert.Equal(new[] { "observer", "operator", "moderator", "admin", "owner" },
            ServerAccessCatalog.Roles.Select(ServerAccessCatalog.RoleId));
    }

    [Fact]
    public void AGrantSurvivesBeingSavedAndReloaded()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcsl-grant-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var saved = new ServerGrant(ServerRole.Operator, ServerPermission.ViewConsole, ServerPermission.StartStop);
            AtomicJsonFile.Write(path, saved);

            var (back, _) = AtomicJsonFile.Load<ServerGrant>(path);

            Assert.Equal(saved, back);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp */ }
        }
    }
}
