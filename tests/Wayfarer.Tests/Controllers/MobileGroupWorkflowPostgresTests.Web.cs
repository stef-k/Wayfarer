using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class MobileGroupWorkflowPostgresTests
{
    /// <summary>One live-pending rule governs replacement, literal lookup and both Web notification reads.</summary>
    [PostgresFact]
    public async Task WebAndMobile_ReplaceElapsedPendingRowsAndPreserveLegacyCleanup()
    {
        await using var db = fixture.CreateContext();
        var owner = await SeedBearerAsync(db);
        var target = await SeedBearerAsync(db);
        var literal = $@"%_\{Guid.NewGuid():N}";
        await db.Users.Where(u => u.Id == target).ExecuteUpdateAsync(set => set.SetProperty(u => u.DisplayName, literal));
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), ConfigureWebServices);
        using var manager = Mobile(app.GetTestClient(), owner);
        using var recipient = Mobile(app.GetTestClient(), target);
        using var ownerWeb = app.GetTestClient();
        using var targetWeb = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(ownerWeb, app.Services, owner);
        UserConnectionTokenHttpTests.Cookie(targetWeb, app.Services, target);

        var groupId = await CreateAsync(manager, "Family");
        var group = $"/api/mobile/groups/{groupId}";
        var inviteService = new InvitationService(db);
        var old = await inviteService.InviteUserAsync(groupId, owner, target, DateTime.UtcNow.AddMinutes(-1));
        var legacy = new GroupInvitation
        {
            Id = Guid.NewGuid(), GroupId = groupId, InviterUserId = owner, Token = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow.AddHours(-1), ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.GroupInvitations.Add(legacy);
        await db.SaveChangesAsync();

        await CheckWebPendingAsync(targetWeb, expected: null);
        var raw = await JsonAsync(await recipient.GetAsync($"/api/mobile/invitations/{old.Id}"), HttpStatusCode.OK);
        Assert.Equal(3, raw.EnumerateObject().Count());
        Assert.Equal("Expired", raw.GetProperty("status").GetString());
        await CodeAsync(await recipient.PostAsync($"/api/mobile/invitations/{old.Id}/decline", null), HttpStatusCode.Conflict, "invitation_expired");

        var scopedSearch = $"/api/users/search?groupId={groupId}";
        using var shortUnauthorized = await targetWeb.GetAsync(scopedSearch + "&query=x");
        Assert.Equal(HttpStatusCode.Forbidden, shortUnauthorized.StatusCode);
        using var shortMissing = await ownerWeb.GetAsync($"/api/users/search?groupId={Guid.NewGuid()}&query=x");
        Assert.Equal(HttpStatusCode.NotFound, shortMissing.StatusCode);
        var query = Uri.EscapeDataString(literal[..7]);
        var picks = await JsonAsync(await ownerWeb.GetAsync(scopedSearch + "&query=" + query), HttpStatusCode.OK);
        Assert.Equal(target, Assert.Single(picks.EnumerateArray()).GetProperty("id").GetString());

        var path = "/User/Groups/InviteAjax";
        using var noCsrf = await ownerWeb.PostAsync(path, InviteForm(groupId, target));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(ownerWeb);
        var replacement = await JsonAsync(await ownerWeb.PostAsync(path, InviteForm(groupId, target)), HttpStatusCode.OK);
        Assert.True(replacement.GetProperty("success").GetBoolean());
        var replacementId = replacement.GetProperty("invite").GetProperty("id").GetGuid();
        await CheckWebPendingAsync(targetWeb, replacementId);
        await CodeAsync(await manager.PostAsJsonAsync(group + "/invitations", new { inviteeUserId = target }),
            HttpStatusCode.Conflict, "pending_exists");
        using var webDuplicate = await ownerWeb.PostAsync(path, InviteForm(groupId, target));
        Assert.Equal(HttpStatusCode.Conflict, webDuplicate.StatusCode);
        await ExpectMembersRowsAsync(ownerWeb, groupId, replacementId, legacy.Id, old.Id);

        await db.GroupInvitations.Where(i => i.Id == replacementId)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        var mobile = await InviteAsync(manager, group, target);
        var live = await JsonAsync(await manager.GetAsync(group + "/invitations"), HttpStatusCode.OK);
        Assert.EndsWith(Assert.Single(live.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid().ToString(), mobile);
        await db.Users.Where(u => u.Id == target).ExecuteUpdateAsync(set => set.SetProperty(u => u.IsActive, false));
        var addressed = await JsonAsync(await manager.GetAsync(group + "/invitations"), HttpStatusCode.OK);
        Assert.Equal(target, Assert.Single(addressed.GetProperty("items").EnumerateArray()).GetProperty("inviteeUserId").GetString());
        await CodeAsync(await manager.PostAsJsonAsync(group + "/invitations", new { inviteeUserId = target }),
            HttpStatusCode.NotFound, "invitee_unavailable");
        await CodeAsync(await manager.PostAsync(group + $"/invitations/{legacy.Id}/revoke", null),
            HttpStatusCode.NotFound, "invitation_unavailable");
        using var revoke = await ownerWeb.PostAsync("/User/Groups/RevokeInviteAjax",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["groupId"] = groupId.ToString(), ["inviteId"] = legacy.Id.ToString() }));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(GroupInvitation.InvitationStatuses.Expired, await db.GroupInvitations.Where(i => i.Id == old.Id).Select(i => i.Status).SingleAsync());
        Assert.Equal(GroupInvitation.InvitationStatuses.Expired, await db.GroupInvitations.Where(i => i.Id == replacementId).Select(i => i.Status).SingleAsync());
    }

    /// <summary>Blank IDs reach the application error page and unavailable selections receive the picker message.</summary>
    [PostgresFact]
    public async Task InviteAjax_PreservesBlankIdErrorAndExplainsUnavailableSelections()
    {
        await using var db = fixture.CreateContext();
        var owner = await SeedBearerAsync(db);
        var inactive = await SeedBearerAsync(db);
        var deleted = await SeedBearerAsync(db);
        var group = await new GroupService(db).CreateGroupAsync(owner, $"Invite-{Guid.NewGuid():N}", null);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), ConfigureWebServices,
            configureApp: host =>
            {
                host.UseExceptionHandler("/Home/Error");
                host.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
            });
        using var web = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(web, app.Services, owner);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(web);
        await db.Users.Where(u => u.Id == inactive).ExecuteUpdateAsync(set => set.SetProperty(u => u.IsActive, false));
        await db.Users.Where(u => u.Id == deleted).ExecuteDeleteAsync();

        const string path = "/User/Groups/InviteAjax";
        foreach (var unavailable in new[] { inactive, deleted })
        {
            using var response = await web.PostAsync(path, InviteForm(group.Id, unavailable));
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var json = await JsonAsync(response, HttpStatusCode.BadRequest);
            Assert.False(json.GetProperty("success").GetBoolean());
            Assert.Equal("This account is no longer available. Select another account.", json.GetProperty("message").GetString());
            Assert.False(await db.GroupInvitations.AnyAsync(i => i.GroupId == group.Id));
        }

        using var blank = await web.PostAsync(path, InviteForm(group.Id, string.Empty));
        Assert.Equal(HttpStatusCode.InternalServerError, blank.StatusCode);
        Assert.Equal("text/html", blank.Content.Headers.ContentType?.MediaType);
        Assert.Contains("An error occurred while processing your request.", await blank.Content.ReadAsStringAsync());
        Assert.False(await db.GroupInvitations.AnyAsync(i => i.GroupId == group.Id));
    }

    /// <summary>Supplies the shared Razor layout's startup settings while keeping request data in PostgreSQL.</summary>
    private void ConfigureWebServices(IServiceCollection services)
    {
        services.AddScoped(_ => fixture.CreateContext());
        services.AddSingleton(Mock.Of<IApplicationSettingsService>(settings => settings.GetSettings() == new ApplicationSettings()));
    }

    /// <summary>Posts the Web application's existing exact-account form contract.</summary>
    private static FormUrlEncodedContent InviteForm(Guid groupId, string target) =>
        new(new Dictionary<string, string> { ["groupId"] = groupId.ToString(), ["inviteeUserId"] = target });

    /// <summary>Both notification consumers filter elapsed rows while preserving their projections.</summary>
    private static async Task CheckWebPendingAsync(HttpClient client, Guid? expected)
    {
        var api = await JsonAsync(await client.GetAsync("/api/invitations"), HttpStatusCode.OK);
        var activity = await JsonAsync(await client.GetAsync("/api/users/activity"), HttpStatusCode.OK);
        foreach (var list in new[] { api, activity.GetProperty("invites") })
        {
            if (expected is null) Assert.Empty(list.EnumerateArray());
            else Assert.Equal(expected, Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());
        }
    }

    /// <summary>The authorized Members markup is the source of the durable post-Invite rows.</summary>
    private static async Task ExpectMembersRowsAsync(HttpClient client, Guid groupId, Guid replacement, Guid legacy, Guid old)
    {
        using var response = await client.GetAsync($"/User/Groups/Members?groupId={groupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"""data-invite-id="{replacement}" """.TrimEnd(), html);
        Assert.Contains($"""data-invite-id="{legacy}" """.TrimEnd(), html);
        Assert.DoesNotContain($"""data-invite-id="{old}" """.TrimEnd(), html);
    }
}
