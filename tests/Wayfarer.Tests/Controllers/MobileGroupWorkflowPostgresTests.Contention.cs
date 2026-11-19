using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class MobileGroupWorkflowPostgresTests
{
    /// <summary>A waiting bearer admission sees the committed Web policy and refuses stale disclosure.</summary>
    [PostgresFact]
    public async Task AcceptWaitingForWebPolicy_RefusesChangedDisclosureWithoutAdmission()
    {
        await using var db = fixture.CreateContext();
        var owner = await SeedBearerAsync(db);
        var target = await SeedBearerAsync(db);
        var group = await new GroupService(db).CreateGroupAsync(owner, $"Policy-{Guid.NewGuid():N}", null, "Organization");
        var invite = await new InvitationService(db).InviteUserAsync(group.Id, owner, target, null);
        var invitation = $"/api/mobile/invitations/{invite.Id}";
        var writer = new HoldSave();
        var waiting = new ObserveWriteLock("Groups", "FOR UPDATE");
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
            services.AddScoped(sp => fixture.CreateContext(
                sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Path.Value?.EndsWith("/org-peer-visibility") == true
                    ? writer : waiting)));
        using var recipient = Mobile(app.GetTestClient(), target);
        using var web = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(web, app.Services, owner);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(web);
        var old = await DisclosureAsync(recipient, invitation);
        Assert.False(old.GetProperty("orgPeerVisibilityEnabled").GetBoolean());

        var change = web.PostAsJsonAsync($"/api/groups/{group.Id}/settings/org-peer-visibility", new { enabled = true });
        Task<HttpResponseMessage>? accept = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await writer.Entered.Task.WaitAsync(deadline.Token);
            accept = recipient.PostAsJsonAsync(invitation + "/accept", new { expected = old }, deadline.Token);
            await WaitForWriteLockAsync(db, waiting, deadline.Token);
        }
        finally
        {
            writer.Release.TrySetResult();
            await JsonAsync(await change.WaitAsync(deadline.Token), HttpStatusCode.OK);
        }

        Assert.NotNull(accept);
        await CodeAsync(await accept, HttpStatusCode.Conflict, "disclosure_changed");
        Assert.False(await db.GroupMembers.AnyAsync(m => m.GroupId == group.Id && m.UserId == target));
        Assert.False(await db.AuditLogs.AnyAsync(a => a.UserId == target && a.Action == "InviteAccept"));
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending,
            await db.GroupInvitations.Where(i => i.Id == invite.Id).Select(i => i.Status).SingleAsync());
        Assert.True((await DisclosureAsync(recipient, invitation)).GetProperty("orgPeerVisibilityEnabled").GetBoolean());
    }

    /// <summary>Refresh cannot mix an earlier role/inclusion with policy committed during the read.</summary>
    [PostgresFact]
    public async Task GroupCollectionRefresh_ReadsOneSnapshot()
    {
        await using var writer = fixture.CreateContext();
        var owner = await SeedBearerAsync(writer);
        var manager = await SeedBearerAsync(writer);
        var groups = new GroupService(writer);
        var group = await groups.CreateGroupAsync(owner, $"Snapshot-{Guid.NewGuid():N}", null, "Organization");
        await groups.AddMemberAsync(group.Id, owner, manager, GroupMember.Roles.Manager);
        var interleave = new AfterBearerRead(async () =>
        {
            await using var change = await writer.Database.BeginTransactionAsync();
            await writer.Groups.Where(g => g.Id == group.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(g => g.OrgPeerVisibilityEnabled, true));
            await writer.GroupMembers.Where(m => m.GroupId == group.Id && m.UserId == manager)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, GroupMember.Roles.Member));
            await change.CommitAsync();
        });
        await using var app = await IdentityRouteHost.StartAsync(writer, CreateTestDirectory(),
            services => services.AddScoped(_ => fixture.CreateContext(interleave)));
        using var client = Mobile(app.GetTestClient(), manager);

        var old = Assert.Single((await JsonAsync(await client.GetAsync("/api/mobile/groups?scope=managed"),
            HttpStatusCode.OK)).EnumerateArray());
        Assert.True(interleave.Fired);
        Assert.True(old.GetProperty("isManager").GetBoolean());
        Assert.False(old.GetProperty("orgPeerVisibilityEnabled").GetBoolean());
        var fresh = await SummaryAsync(client);
        Assert.True(fresh.GetProperty("isMember").GetBoolean());
        Assert.True(fresh.GetProperty("orgPeerVisibilityEnabled").GetBoolean());
        Assert.Empty((await JsonAsync(await client.GetAsync("/api/mobile/groups?scope=managed"),
            HttpStatusCode.OK)).EnumerateArray());
    }

    /// <summary>Suspends the actual Web policy writer with the Group lock still held.</summary>
    private sealed class HoldSave : SaveChangesInterceptor
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

    /// <summary>Verifies an actual database wait before the competing transaction can commit.</summary>
    private static async Task WaitForWriteLockAsync(ApplicationDbContext observer, ObserveWriteLock waiting, CancellationToken ct)
    {
        var pid = await waiting.Backend.Task.WaitAsync(ct);
        while (!await observer.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({pid})) > 0 AS \"Value\"")
            .SingleAsync(ct))
            await Task.Delay(10, ct);
    }

    /// <summary>Identifies the backend executing the relevant production Group or account lock.</summary>
    private sealed class ObserveWriteLock(string table, string mode) : DbCommandInterceptor
    {
        public TaskCompletionSource<int> Backend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Observe(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Observe(command);
            return ValueTask.FromResult(result);
        }

        /// <summary>Signals only a matching lock, without retaining request values.</summary>
        private void Observe(DbCommand command)
        {
            if (command.CommandText.Contains($"FROM \"{table}\"") && command.CommandText.Contains(mode)
                && command.Connection is NpgsqlConnection connection)
                Backend.TrySetResult(connection.ProcessID);
        }
    }

    /// <summary>Commits a different snapshot precisely between bearer lookup and Group collection data.</summary>
    private sealed class AfterBearerRead(Func<Task> change) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("FROM \"AspNetUsers\"") && command.CommandText.Contains("\"ApiTokens\""))
            {
                Fired = true;
                await change();
            }
            return result;
        }
    }
}
