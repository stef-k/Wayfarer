using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Durable invitation/departure ordering through real services and the established PostgreSQL fixture.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class InvitationMembershipPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Both lock orders end departed; a waiting acceptance never restores a committed departure.</summary>
    [PostgresTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcceptanceAndDepartureSerialize(bool departureFirst, bool leave)
    {
        var (group, owner, target, invitation) = await SeedAsync(active: departureFirst);
        var gate = new SaveGate();
        await using var firstDb = fixture.CreateContext(gate);
        await using var waitingDb = fixture.CreateContext();
        await waitingDb.Database.OpenConnectionAsync();
        // Deliberately retain earlier tracked state in the waiting request.
        await waitingDb.GroupInvitations.SingleAsync(i => i.Id == invitation.Id);
        await waitingDb.GroupMembers.SingleAsync(m => m.GroupId == group.Id && m.UserId == target.Id);
        Task<Exception?>? first = null;
        Task<Exception?>? waiting = null;
        try
        {
            first = Record.ExceptionAsync(() => departureFirst
                ? DepartAsync(firstDb, group.Id, owner.Id, target.Id, leave)
                : new InvitationService(firstDb).AcceptAsync(invitation.Token, target.Id));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiting = Record.ExceptionAsync(() => departureFirst
                ? new InvitationService(waitingDb).AcceptAsync(invitation.Token, target.Id)
                : DepartAsync(waitingDb, group.Id, owner.Id, target.Id, leave));
            await WaitForLockAsync(((NpgsqlConnection)waitingDb.Database.GetDbConnection()).ProcessID);
            Assert.False(waiting.IsCompleted);
            gate.Release.SetResult();
            Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            var failure = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            if (departureFirst) Assert.IsType<InvalidOperationException>(failure);
            else Assert.Null(failure);

            await using var persisted = fixture.CreateContext();
            var membership = await persisted.GroupMembers.SingleAsync(m => m.GroupId == group.Id && m.UserId == target.Id);
            Assert.Equal(leave ? GroupMember.MembershipStatuses.Left : GroupMember.MembershipStatuses.Removed, membership.Status);
            Assert.NotNull(membership.LeftAt);
            var history = await persisted.GroupInvitations.SingleAsync(i => i.Id == invitation.Id);
            Assert.Equal(departureFirst ? GroupInvitation.InvitationStatuses.Revoked : GroupInvitation.InvitationStatuses.Accepted,
                history.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new InvitationService(persisted).AcceptAsync(invitation.Token, target.Id));
            var fresh = await new InvitationService(persisted).InviteUserAsync(group.Id, owner.Id, target.Id, null);
            var rejoined = await new InvitationService(persisted).AcceptAsync(fresh.Token, target.Id);
            Assert.Equal(GroupMember.Roles.Member, rejoined.Role);
            Assert.Equal(GroupMember.MembershipStatuses.Active, rejoined.Status);
            Assert.True(rejoined.OrgPeerVisibilityAccessDisabled);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (first is not null) await first;
            if (waiting is not null) await waiting;
            await DeleteGroupAsync(group.Id);
        }
    }

    /// <summary>A failure after SQL persistence rolls back departure, invitation invalidation and audit together.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDepartureRollsBackInvitationsAndPublishesNothing(bool leave)
    {
        var (group, owner, target, invitation) = await SeedAsync(active: true);
        try
        {
            await using var mutation = fixture.CreateContext(new RejectSavedChanges());
            var sse = new RecordingSseService();
            var controller = Controller(mutation, leave ? target.Id : owner.Id, sse);

            await Assert.ThrowsAsync<DbUpdateException>(() => leave
                ? controller.Leave(group.Id, default)
                : controller.RemoveMember(group.Id, target.Id, default));

            Assert.Empty(sse.Messages);
            await using var persisted = fixture.CreateContext();
            Assert.Equal(GroupMember.MembershipStatuses.Active,
                (await persisted.GroupMembers.SingleAsync(m => m.GroupId == group.Id && m.UserId == target.Id)).Status);
            var pending = await persisted.GroupInvitations.SingleAsync(i => i.Id == invitation.Id);
            Assert.Equal(GroupInvitation.InvitationStatuses.Pending, pending.Status);
            Assert.Null(pending.RespondedAt);
            Assert.False(await persisted.AuditLogs.AnyAsync(a => a.Details!.Contains(group.Id.ToString())
                && (a.Action == "MemberRemove" || a.Action == "MemberLeave")));
        }
        finally { await DeleteGroupAsync(group.Id); }
    }

    /// <summary>Organization ownership safeguards leave pending invitations and success notifications untouched.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOwnerDepartureDoesNotRevokeInvitations(bool leave)
    {
        var (group, owner, target, invitation) = await SeedAsync(active: true);
        try
        {
            await using var db = fixture.CreateContext();
            await db.Groups.Where(g => g.Id == group.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.GroupType, "Organization"));
            await db.GroupMembers.Where(m => m.GroupId == group.Id && m.UserId == target.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, GroupMember.Roles.Member));
            await db.GroupInvitations.Where(i => i.Id == invitation.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.InviteeUserId, owner.Id));
            var sse = new RecordingSseService();
            var controller = Controller(db, owner.Id, sse);

            var result = leave ? await controller.Leave(group.Id, default)
                : await controller.RemoveMember(group.Id, owner.Id, default);

            Assert.IsType<ConflictObjectResult>(result);
            Assert.Empty(sse.Messages);
            db.ChangeTracker.Clear();
            Assert.Equal(owner.Id, (await db.Groups.SingleAsync(g => g.Id == group.Id)).OwnerUserId);
            Assert.Equal(GroupInvitation.InvitationStatuses.Pending,
                (await db.GroupInvitations.SingleAsync(i => i.Id == invitation.Id)).Status);
            Assert.Equal(GroupMember.MembershipStatuses.Active,
                (await db.GroupMembers.SingleAsync(m => m.GroupId == group.Id && m.UserId == owner.Id)).Status);
        }
        finally { await DeleteGroupAsync(group.Id); }
    }

    /// <summary>Waits for PostgreSQL to report an actual conflicting lock; timing alone is not ordering evidence.</summary>
    private async Task WaitForLockAsync(int backendId)
    {
        await using var observer = fixture.CreateConnection();
        await observer.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@backend)) > 0", observer);
        command.Parameters.AddWithValue("backend", backendId);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await command.ExecuteScalarAsync() is true) return;
            await Task.Delay(10);
        }
        Assert.Fail("The competing membership operation did not wait for the Group lock.");
    }

    /// <summary>Seeds either legacy active-member history or a legitimate fresh invitation after departure.</summary>
    private async Task<(Group Group, ApplicationUser Owner, ApplicationUser Target, GroupInvitation Invitation)> SeedAsync(bool active)
    {
        var owner = await fixture.CreateUserAsync();
        var target = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var groups = new GroupService(db);
        var group = await groups.CreateGroupAsync(owner.Id, $"Invitation-{Guid.NewGuid()}", null);
        var member = await groups.AddMemberAsync(group.Id, owner.Id, target.Id, GroupMember.Roles.Manager);
        member.JoinedAt = DateTime.UtcNow.AddDays(-1);
        member.OrgPeerVisibilityAccessDisabled = true;
        if (!active)
        {
            member.Status = GroupMember.MembershipStatuses.Removed;
            member.LeftAt = DateTime.UtcNow.AddHours(-1);
        }
        // Fixture accounts already exist in a separate context; retain only their foreign keys.
        var invitation = new GroupInvitation
        {
            Id = Guid.NewGuid(), GroupId = group.Id, InviterUserId = owner.Id,
            InviteeUserId = target.Id, Token = Guid.NewGuid().ToString(), CreatedAt = DateTime.UtcNow
        };
        db.GroupInvitations.Add(invitation);
        await db.SaveChangesAsync();
        return (group, owner, target, invitation);
    }

    /// <summary>Calls the existing departure owner without a nested invitation transaction.</summary>
    private static Task DepartAsync(ApplicationDbContext db, Guid groupId, string ownerId, string targetId, bool leave) =>
        leave ? new GroupService(db).LeaveGroupAsync(groupId, targetId)
            : new GroupService(db).RemoveMemberAsync(groupId, ownerId, targetId);

    /// <summary>Uses the existing API mutation owner with authenticated claims and a recording broadcaster.</summary>
    private static GroupsController Controller(ApplicationDbContext db, string userId, SseService sse) =>
        new(db, new GroupService(db), NullLogger<GroupsController>.Instance, new LocationService(db), sse)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, userId)], "test")) }
            }
        };

    /// <summary>Deletes only the Group created by this test; fixture-owned accounts are cleaned by the fixture.</summary>
    private async Task DeleteGroupAsync(Guid groupId)
    {
        await using var db = fixture.CreateContext();
        await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync();
    }

    /// <summary>Holds the first product mutation immediately before its atomic save, retaining its Group lock.</summary>
    private sealed class SaveGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    /// <summary>Fails after real SQL writes, before the service's transaction commit.</summary>
    private sealed class RejectSavedChanges : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new DbUpdateException("Forced post-save failure");
    }

    /// <summary>Captures every publication attempt for the mutation commit-boundary assertions.</summary>
    private sealed class RecordingSseService : SseService
    {
        public List<string> Messages { get; } = [];
        public override Task BroadcastAsync(string channel, string data)
        {
            Messages.Add(data);
            return Task.CompletedTask;
        }
    }
}
