using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Options;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Real persisted membership authority and controller owners; no tracked-context revalidation.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class GroupSseDeliveryPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Both real owners stop post-commit member-removed/location delivery and release admission.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerStopsDeliveryAfterCommittedRemoval(bool mobile)
    {
        var (group, owner, member) = await SeedAsync();
        using var services = Services();
        var lease = new GroupSseDeliveryLease(services.GetRequiredService<IServiceScopeFactory>());
        await using var db = fixture.CreateContext();
        var persistedGroup = (await db.Groups.FindAsync(group.Id))!;
        persistedGroup.GroupType = "Organization";
        persistedGroup.OrgPeerVisibilityEnabled = true;
        var recipient = await db.GroupMembers.SingleAsync(m => m.GroupId == group.Id && m.UserId == member.Id);
        recipient.OrgPeerVisibilityAccessDisabled = true;
        var token = Guid.NewGuid().ToString();
        db.ApiTokens.Add(new ApiToken { Name = "sse-test", TokenHash = ApiTokenService.HashToken(token),
            UserId = member.Id, User = await db.Users.FindAsync(member.Id) ?? throw new InvalidOperationException(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        using var body = new SseGateStream();
        body.Release.SetResult();
        var http = body.Response().HttpContext;
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, member.Id)], "cookie"));
        var service = new SseService();
        var timeline = new GroupTimelineService(db, new LocationService(db), new ConfigurationBuilder().Build());
        using var request = new CancellationTokenSource();
        Task subscription;
        if (mobile)
        {
            http.User = new ClaimsPrincipal();
            http.Request.Headers.Authorization = $"Bearer {token}";
            var accessor = new MobileCurrentUserAccessor(new HttpContextAccessor { HttpContext = http }, db,
                NullLogger<MobileCurrentUserAccessor>.Instance);
            var controller = new MobileSseController(db, NullLogger<BaseApiController>.Instance, accessor, service,
                timeline, new MobileSseOptions { HeartbeatIntervalMilliseconds = 5 }, lease)
                { ControllerContext = new ControllerContext { HttpContext = http } };
            subscription = controller.SubscribeToGroupAsync(group.Id, request.Token);
        }
        else
        {
            var controller = new SseController(service, db, timeline,
                new MobileSseOptions { HeartbeatIntervalMilliseconds = 5 },
                services.GetRequiredService<IServiceScopeFactory>(), lease)
                { ControllerContext = new ControllerContext { HttpContext = http } };
            subscription = controller.SubscribeToGroupAsync(group.Id, request.Token);
        }
        using var remainingBody = new MemoryStream();
        var remainingResponse = new DefaultHttpContext().Response;
        remainingResponse.Body = remainingBody;
        var remainingSubscription = service.SubscribeAsync($"group-{group.Id}", remainingResponse, request.Token,
            deliveryLease: ct => lease.AcquireAsync(group.Id, owner.Id, ct), resolvedUserId: owner.Id);
        try
        {
            // First heartbeat is a causal registration barrier, not a sleep/poll loop.
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var channel = $"group-{group.Id}";
            await service.BroadcastAsync(channel, JsonSerializer.Serialize(GroupSseEventDto.Location(123, DateTime.UtcNow, owner.Id, "Owner", true)));
            Assert.Contains("locationId", Encoding.UTF8.GetString(body.ToArray()));
            await using (var policy = fixture.CreateContext())
                await policy.GroupMembers.Where(m => m.GroupId == group.Id && m.UserId == owner.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.OrgPeerVisibilityAccessDisabled, true));
            await service.BroadcastAsync(channel, JsonSerializer.Serialize(GroupSseEventDto.Location(778099, DateTime.UtcNow, owner.Id, "hidden-source", true)));
            await service.BroadcastAsync(channel, JsonSerializer.Serialize(GroupSseEventDto.LocationDeleted(778099, owner.Id)));
            Assert.DoesNotContain("778099", Encoding.UTF8.GetString(body.ToArray()));
            Assert.DoesNotContain("hidden-source", Encoding.UTF8.GetString(body.ToArray()));
            await using (var mutation = fixture.CreateContext())
                await new GroupService(mutation).RemoveMemberAsync(group.Id, owner.Id, member.Id);
            await service.BroadcastAsync(channel, JsonSerializer.Serialize(GroupSseEventDto.MemberRemoved(member.Id)));
            await subscription.WaitAsync(TimeSpan.FromSeconds(5));
            var length = body.Length;
            await service.BroadcastAsync(channel, "protected-after-removal");
            Assert.Equal(length, body.Length);
            Assert.DoesNotContain("member-removed", Encoding.UTF8.GetString(body.ToArray()));
            Assert.Contains("member-removed", Encoding.UTF8.GetString(remainingBody.ToArray()));
            Assert.Equal(1, service.ActiveConnectionCount);
        }
        finally
        {
            request.Cancel();
            await Task.WhenAll(subscription, remainingSubscription);
            Assert.Equal(0, service.ActiveConnectionCount);
            await DeleteGroupAsync(group.Id);
        }
    }

    /// <summary>Shared authority covers every persisted revocation shape without duplicating controller journeys.</summary>
    [PostgresTheory]
    [InlineData("left")]
    [InlineData("group")]
    [InlineData("user")]
    [InlineData("archived")]
    public async Task PersistedEligibilityFailsClosed(string change)
    {
        var (group, owner, member) = await SeedAsync();
        using var services = Services();
        var authority = new GroupSseDeliveryLease(services.GetRequiredService<IServiceScopeFactory>());
        try
        {
            await using (var active = await authority.AcquireAsync(group.Id, member.Id, CancellationToken.None))
                Assert.NotNull(active);
            await using (var db = fixture.CreateContext())
            {
                switch (change)
                {
                    case "left": await new GroupService(db).LeaveGroupAsync(group.Id, member.Id); break;
                    case "group": await new GroupService(db).DeleteGroupAsync(group.Id, owner.Id); break;
                    case "user": await db.Users.Where(u => u.Id == member.Id).ExecuteDeleteAsync(); break;
                    case "archived": await db.Groups.Where(g => g.Id == group.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.IsArchived, true)); break;
                }
            }
            Assert.Null(await authority.AcquireAsync(group.Id, member.Id, CancellationToken.None));
        }
        finally { await DeleteGroupAsync(group.Id); }
    }

    /// <summary>A bounded send holds the membership SHARE lock until flush; removal cannot commit first.</summary>
    [PostgresFact]
    public async Task GatedSendOrdersBeforeRemovalCommit()
    {
        var (group, owner, member) = await SeedAsync();
        using var services = Services();
        var authority = new GroupSseDeliveryLease(services.GetRequiredService<IServiceScopeFactory>());
        var service = new SseService();
        using var body = new SseGateStream(flush: true);
        using var request = new CancellationTokenSource();
        var subscription = service.SubscribeAsync("ordered", body.Response(), request.Token,
            deliveryLease: ct => authority.AcquireAsync(group.Id, member.Id, ct));
        try
        {
            var send = service.BroadcastAsync("ordered", "before-commit");
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var mutation = fixture.CreateContext();
            // NOWAIT establishes the conflicting lock deterministically instead of timing a blocked writer.
            await using (var tx = await mutation.Database.BeginTransactionAsync())
            {
                var conflict = await Assert.ThrowsAsync<PostgresException>(() => mutation.Database.ExecuteSqlInterpolatedAsync($"""
                    SELECT 1 FROM "GroupMembers" WHERE "GroupId" = {group.Id} AND "UserId" = {member.Id} FOR UPDATE NOWAIT
                    """));
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, conflict.SqlState);
                await tx.RollbackAsync();
            }
            var removal = new GroupService(mutation).RemoveMemberAsync(group.Id, owner.Id, member.Id);
            body.Release.SetResult();
            await Task.WhenAll(send, removal).WaitAsync(TimeSpan.FromSeconds(5));
            await service.BroadcastAsync("ordered", "after-commit");
            await subscription.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("data: before-commit\n\n", Encoding.UTF8.GetString(body.ToArray()));
        }
        finally
        {
            request.Cancel();
            body.Release.TrySetResult();
            await subscription;
            await DeleteGroupAsync(group.Id);
        }
    }

    /// <summary>Group-then-member NOWAIT acquisition fails closed behind either writer, releasing any earlier lock.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriterConflictFailsClosedAndReleasesLocks(bool groupLock)
    {
        var (group, _, member) = await SeedAsync();
        using var services = Services();
        var authority = new GroupSseDeliveryLease(services.GetRequiredService<IServiceScopeFactory>());
        try
        {
            await using var writer = fixture.CreateContext();
            await using (var transaction = await writer.Database.BeginTransactionAsync())
            {
                if (groupLock)
                    await writer.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Groups" WHERE "Id" = {group.Id} FOR UPDATE""");
                else
                    await writer.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "GroupMembers" WHERE "GroupId" = {group.Id} AND "UserId" = {member.Id} FOR UPDATE""");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var exception = await Assert.ThrowsAnyAsync<Exception>(() => authority.AcquireAsync(group.Id, member.Id, deadline.Token));
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(exception.GetBaseException()).SqlState);
                await transaction.RollbackAsync();
            }
            await using var recovered = await authority.AcquireAsync(group.Id, member.Id, CancellationToken.None);
            Assert.NotNull(recovered);
        }
        finally { await DeleteGroupAsync(group.Id); }
    }

    private ServiceProvider Services() => new ServiceCollection()
        .AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();

    /// <summary>A queued frame behind a heartbeat must reread committed policy and hide all source metadata.</summary>
    [PostgresTheory]
    [InlineData("opt-out")]
    [InlineData("switch-off")]
    [InlineData("source-removed")]
    public async Task QueuedLocationMetadataRespectsCommittedPolicy(string change)
    {
        var (group, owner, member) = await SeedAsync();
        await using (var db = fixture.CreateContext())
            await db.Groups.Where(g => g.Id == group.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.GroupType, "Organization")
                .SetProperty(g => g.OrgPeerVisibilityEnabled, true));
        using var services = Services();
        var authority = new GroupSseDeliveryLease(services.GetRequiredService<IServiceScopeFactory>());
        var service = new SseService();
        using var body = new SseGateStream(flush: true);
        using var request = new CancellationTokenSource();
        var subscription = service.SubscribeAsync("privacy", body.Response(), request.Token,
            enableHeartbeat: true, heartbeatInterval: TimeSpan.FromMilliseconds(20), resolvedUserId: member.Id,
            eventDeliveryLease: (data, ct) => authority.AcquireEventAsync(group.Id, member.Id, data, ct));
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var data = JsonSerializer.Serialize(GroupSseEventDto.Location(778001, DateTime.UtcNow, owner.Id, "hidden-source", true));
            var queued = service.BroadcastAsync("privacy", data);
            Assert.False(queued.IsCompleted);
            await using (var db = fixture.CreateContext())
            {
                if (change == "switch-off")
                    await db.Groups.Where(g => g.Id == group.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.OrgPeerVisibilityEnabled, false));
                else if (change == "opt-out")
                    await db.GroupMembers.Where(m => m.GroupId == group.Id && m.UserId == owner.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.OrgPeerVisibilityAccessDisabled, true));
                else
                    await db.GroupMembers.Where(m => m.GroupId == group.Id && m.UserId == owner.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, GroupMember.MembershipStatuses.Removed));
            }
            body.Release.SetResult();
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            await service.BroadcastAsync("privacy", JsonSerializer.Serialize(GroupSseEventDto.LocationDeleted(778001, owner.Id)));
            var bytes = Encoding.UTF8.GetString(body.ToArray());
            Assert.DoesNotContain(owner.Id, bytes);
            Assert.DoesNotContain("locationId", bytes);
            Assert.DoesNotContain("timestampUtc", bytes);
            await service.BroadcastAsync("privacy", JsonSerializer.Serialize(GroupSseEventDto.MemberJoined(member.Id)));
            await service.BroadcastAsync("privacy", JsonSerializer.Serialize(GroupSseEventDto.Location(778002, DateTime.UtcNow, member.Id, "self", true)));
            Assert.Contains("member-joined", Encoding.UTF8.GetString(body.ToArray()));
            Assert.Contains("778002", Encoding.UTF8.GetString(body.ToArray()));
            Assert.Equal(1, service.ActiveConnectionCount);
        }
        finally
        {
            request.Cancel();
            body.Release.TrySetResult();
            await subscription;
            await DeleteGroupAsync(group.Id);
        }
    }

    /// <summary>Location frames hold both the source membership and group policy stable through flush.</summary>
    [PostgresFact]
    public async Task SourceAndPolicyWritesConflictWithProtectedFrame()
    {
        var (group, owner, member) = await SeedAsync();
        using var services = Services();
        var authority = new GroupSseDeliveryLease(services.GetRequiredService<IServiceScopeFactory>());
        try
        {
            var frame = JsonSerializer.Serialize(GroupSseEventDto.Location(778003, DateTime.UtcNow, owner.Id, "owner", true));
            await using var eligible = (await authority.AcquireEventAsync(group.Id, member.Id, frame, default)).Lease;
            Assert.NotNull(eligible);
            await using var writer = fixture.CreateContext();
            foreach (var sourceLock in new[] { true, false })
            {
                await using var tx = await writer.Database.BeginTransactionAsync();
                var conflict = await Assert.ThrowsAsync<PostgresException>(() => sourceLock
                    ? writer.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "GroupMembers" WHERE "GroupId" = {group.Id} AND "UserId" = {owner.Id} FOR UPDATE NOWAIT""")
                    : writer.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Groups" WHERE "Id" = {group.Id} FOR UPDATE NOWAIT"""));
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, conflict.SqlState);
                await tx.RollbackAsync();
            }
        }
        finally { await DeleteGroupAsync(group.Id); }
    }

    private async Task<(Group Group, ApplicationUser Owner, ApplicationUser Member)> SeedAsync()
    {
        var owner = await fixture.CreateUserAsync();
        var member = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, $"SSE-{Guid.NewGuid()}", null);
        await new GroupService(db).AddMemberAsync(group.Id, owner.Id, member.Id, GroupMember.Roles.Member);
        return (group, owner, member);
    }

    private async Task DeleteGroupAsync(Guid groupId)
    {
        await using var db = fixture.CreateContext();
        await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync();
    }
}
