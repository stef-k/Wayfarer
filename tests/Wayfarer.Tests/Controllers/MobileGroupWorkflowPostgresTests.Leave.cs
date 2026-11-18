using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class MobileGroupWorkflowPostgresTests
{
    /// <summary>Choice, account cascades and explicit disband are rechecked at the same Leave decision.</summary>
    [PostgresFact]
    public async Task OwnerSelectsSuccessor_AndOnlyAReviewedDisbandDeletesTheGroup()
    {
        await using var db = fixture.CreateContext();
        var owner = await SeedBearerAsync(db);
        var first = await SeedBearerAsync(db);
        var second = await SeedBearerAsync(db);
        var deleted = await SeedBearerAsync(db);
        var groups = new GroupService(db);
        var group = await groups.CreateGroupAsync(owner, $"Transfer-{Guid.NewGuid():N}", null, "Friends");
        foreach (var peer in new[] { first, second, deleted })
            await groups.AddMemberAsync(group.Id, owner, peer, GroupMember.Roles.Member);

        var waiting = new ObserveWriteLock("AspNetUsers", "FOR SHARE");
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddScoped(_ => fixture.CreateContext(waiting)));
        using var client = Mobile(app.GetTestClient(), owner);
        var path = $"/api/mobile/groups/{group.Id}";
        var preview = await JsonAsync(await client.GetAsync(path + "/leave"), HttpStatusCode.OK);
        Assert.Equal(3, preview.GetProperty("successorCandidates").GetArrayLength());

        var stale = await ChooseAsync(client, path, deleted);
        await DeleteSuccessorDuringLeaveAsync(client, path, deleted, stale, waiting);
        Assert.Equal(owner, await db.Groups.Where(g => g.Id == group.Id).Select(g => g.OwnerUserId).SingleAsync());

        var selected = await ChooseAsync(client, path, second);
        var collision = await groups.CreateGroupAsync(second, group.Name, null, "Family");
        await CodeAsync(await client.PostAsJsonAsync(path + "/leave", new { expected = selected, successorUserId = second }),
            HttpStatusCode.Conflict, "leave_changed");
        var blocked = await ChooseAsync(client, path, second);
        Assert.Equal("successor_name_conflict", blocked.GetProperty("blockedReason").GetString());
        await CodeAsync(await client.PostAsJsonAsync(path + "/leave", new { expected = blocked, successorUserId = second }),
            HttpStatusCode.Conflict, "successor_name_conflict");
        await groups.DeleteGroupAsync(collision.Id, second);

        var transfer = await JsonAsync(await client.PostAsJsonAsync(path + "/leave", new { expected = selected, successorUserId = second }),
            HttpStatusCode.OK);
        Assert.Equal(second, transfer.GetProperty("successorUserId").GetString());
        Assert.Equal(second, await db.Groups.Where(g => g.Id == group.Id).Select(g => g.OwnerUserId).SingleAsync());

        using var newOwner = Mobile(app.GetTestClient(), second);
        var old = await LeaveReviewAsync(newOwner, path);
        await db.Users.Where(u => u.Id == first).ExecuteUpdateAsync(set => set.SetProperty(u => u.IsActive, false));
        await CodeAsync(await newOwner.PostAsJsonAsync(path + "/leave", new { expected = old }),
            HttpStatusCode.Conflict, "leave_changed");
        var disband = await LeaveReviewAsync(newOwner, path);
        Assert.Equal("DisbandGroup", disband.GetProperty("impact").GetString());
        var original = await StoreOlderLocationAsync(db, first);
        var after = await JsonAsync(await newOwner.PostAsJsonAsync(path + "/leave", new { expected = disband }), HttpStatusCode.OK);
        Assert.Equal("DisbandGroup", after.GetProperty("impact").GetString());
        Assert.False(await db.Groups.AnyAsync(g => g.Id == group.Id));
        Assert.False(await db.GroupMembers.AnyAsync(m => m.GroupId == group.Id));
        Assert.True(await db.Locations.AnyAsync(row => row.Id == original.Id));
    }

    /// <summary>Interleaves a real account cascade with Leave before the current decision is evaluated.</summary>
    private async Task DeleteSuccessorDuringLeaveAsync(HttpClient client, string group, string successor,
        JsonElement expected, ObserveWriteLock waiting)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var writer = fixture.CreateContext();
        await using var observer = fixture.CreateContext();
        await using var deletion = await writer.Database.BeginTransactionAsync(deadline.Token);
        await writer.Users.Where(u => u.Id == successor).ExecuteDeleteAsync(deadline.Token);
        var leave = client.PostAsJsonAsync(group + "/leave", new { expected, successorUserId = successor }, deadline.Token);
        try { await WaitForWriteLockAsync(observer, waiting, deadline.Token); }
        finally { await deletion.CommitAsync(); }
        await CodeAsync(await leave, HttpStatusCode.Conflict, "leave_changed");
    }

    /// <summary>Reads a deliberate successor's exact impact before submitting the review.</summary>
    private static async Task<JsonElement> ChooseAsync(HttpClient client, string path, string user) =>
        (await JsonAsync(await client.GetAsync(path + "/leave?successorUserId=" + Uri.EscapeDataString(user)), HttpStatusCode.OK))
            .GetProperty("review");
}
