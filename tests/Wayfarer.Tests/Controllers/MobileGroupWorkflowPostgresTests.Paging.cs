using System.Net;
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
    /// <summary>Keyset continuation respects UUID ties and is bound only after current scope is authorized.</summary>
    [PostgresFact]
    public async Task InvitationCursor_PagesThenReauthorizesBeforeCheckingBinding()
    {
        await using var db = fixture.CreateContext();
        var owner = await SeedBearerAsync(db);
        var peer = await SeedBearerAsync(db);
        var groups = new GroupService(db);
        var managed = await groups.CreateGroupAsync(owner, $"Managed-{Guid.NewGuid():N}", null, "Family");
        await groups.AddMemberAsync(managed.Id, owner, peer, GroupMember.Roles.Manager);
        var created = DateTime.UtcNow.AddMinutes(-1);
        for (var index = 0; index < 51; index++)
        {
            var group = new Group { Id = Guid.NewGuid(), OwnerUserId = owner, Name = $"Inbox-{Guid.NewGuid():N}" };
            db.Groups.Add(group);
            db.GroupInvitations.Add(new GroupInvitation
            {
                Id = Guid.NewGuid(), GroupId = group.Id, InviterUserId = owner,
                InviteeUserId = peer, Token = Guid.NewGuid().ToString(), CreatedAt = created
            });
        }
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddScoped(_ => fixture.CreateContext()));
        using var recipient = Mobile(app.GetTestClient(), peer);
        using var ownerClient = Mobile(app.GetTestClient(), owner);
        using var cookie = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(cookie, app.Services, peer);
        using var unauthenticated = await cookie.GetAsync("/api/mobile/invitations");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var malformed = await cookie.GetAsync("/api/mobile/invitations?cursor=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var inbox = "/api/mobile/invitations";
        var first = await JsonAsync(await recipient.GetAsync(inbox), HttpStatusCode.OK);
        Assert.Equal(50, first.GetProperty("items").GetArrayLength());
        var cursor = "?cursor=" + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
        var next = await JsonAsync(await recipient.GetAsync(inbox + cursor), HttpStatusCode.OK);
        var tail = Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        var expectedTail = await db.GroupInvitations.Where(i => i.InviteeUserId == peer)
            .OrderBy(i => i.Id).Select(i => i.Id).FirstAsync();
        Assert.Equal(expectedTail, tail);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);
        await CodeAsync(await ownerClient.GetAsync(inbox + cursor), HttpStatusCode.BadRequest, "cursor_scope_mismatch");

        var list = $"/api/mobile/groups/{managed.Id}/invitations";
        await CodeAsync(await recipient.GetAsync(list + cursor), HttpStatusCode.BadRequest, "cursor_scope_mismatch");
        await db.GroupMembers.Where(m => m.GroupId == managed.Id && m.UserId == peer)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, GroupMember.Roles.Member));
        await CodeAsync(await recipient.GetAsync(list + cursor), HttpStatusCode.Forbidden, "group_role_required");
        await CodeAsync(await ownerClient.PostAsync(list + $"/{tail}/revoke", null), HttpStatusCode.NotFound, "invitation_unavailable");
    }
}
