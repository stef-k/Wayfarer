using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.TestHost;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class MobileGroupWorkflowPostgresTests
{
    /// <summary>Checks localized role actions, plain-text descriptions, and matching invite refusals.</summary>
    [PostgresFact]
    public async Task GroupWebLanguage_PreservesActionsAndLocalizesRedirectDestination()
    {
        await using var db = fixture.CreateContext();
        var owner = await SeedBearerAsync(db);
        var manager = await SeedBearerAsync(db);
        var member = await SeedBearerAsync(db);
        var invitee = await SeedBearerAsync(db);
        var name = $"Group <{Guid.NewGuid():N}>";
        const string description = "Group <strong>description</strong> & notes";
        var group = await new GroupService(db).CreateGroupAsync(owner, name, description, "Organization");
        db.GroupMembers.AddRange(
            new GroupMember { GroupId = group.Id, UserId = manager, Role = GroupMember.Roles.Manager },
            new GroupMember { GroupId = group.Id, UserId = member, Role = GroupMember.Roles.Member });
        await db.SaveChangesAsync();

        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), ConfigureWebServices);
        using var ownerWeb = app.GetTestClient();
        using var managerWeb = app.GetTestClient();
        using var memberWeb = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(ownerWeb, app.Services, owner, "Manager");
        UserConnectionTokenHttpTests.Cookie(managerWeb, app.Services, manager, "Manager");
        UserConnectionTokenHttpTests.Cookie(memberWeb, app.Services, member);
        ownerWeb.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        managerWeb.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        memberWeb.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");

        var selector = $"tr[data-group-id='{group.Id}']";
        var ownerIndex = await GroupDocumentAsync(await ownerWeb.GetAsync("/Manager/Groups"));
        Assert.Equal(name, ownerIndex.QuerySelector(selector + " td")?.TextContent);
        Assert.Equal("Επεξεργασία", ownerIndex.QuerySelector(selector + " a[href*='/Edit/']")?.TextContent);
        Assert.Equal("Διαγραφή", ownerIndex.QuerySelector(selector + " form button")?.TextContent);
        var managerIndex = await GroupDocumentAsync(await managerWeb.GetAsync("/Manager/Groups"));
        Assert.Equal("Μέλη", managerIndex.QuerySelector(selector + " a[href*='/Members']")?.TextContent);
        Assert.Null(managerIndex.QuerySelector(selector + " a[href*='/Edit/']"));
        Assert.Null(managerIndex.QuerySelector(selector + " form"));
        var memberIndex = await GroupDocumentAsync(await memberWeb.GetAsync("/User/Groups"));
        var descriptionCell = Assert.IsAssignableFrom<IElement>(memberIndex.QuerySelector(selector + " td:nth-child(2)"));
        Assert.Equal(description, descriptionCell.TextContent);
        Assert.Empty(descriptionCell.Children);
        Assert.Equal("Χάρτης", memberIndex.QuerySelector(selector + " a[href*='/Map']")?.TextContent);
        Assert.Equal("Αποχώρηση", memberIndex.QuerySelector(selector + " .js-leave")?.TextContent);
        Assert.Null(memberIndex.QuerySelector(selector + " a[href*='/Members']"));

        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(managerWeb);
        var created = await JsonAsync(await managerWeb.PostAsync("/Manager/Groups/InviteAjax", InviteForm(group.Id, invitee)), HttpStatusCode.OK);
        var id = created.GetProperty("invite").GetProperty("id").GetGuid();
        var members = await GroupDocumentAsync(await managerWeb.GetAsync($"/Manager/Groups/Members?groupId={group.Id}"));
        Assert.Equal("Διαχειριστής ομάδας", members.QuerySelector($"tr[data-user-id='{manager}'] td:nth-child(3)")?.TextContent);
        Assert.Equal("Μέλος", members.QuerySelector($"tr[data-user-id='{member}'] td:nth-child(3)")?.TextContent);
        Assert.Equal("Αφαίρεση", members.QuerySelector($"tr[data-user-id='{member}'] button")?.TextContent);
        Assert.Null(members.QuerySelector($"tr[data-user-id='{owner}'] button"));
        Assert.Equal("Ακύρωση πρόσκλησης", members.QuerySelector($"tr[data-invite-id='{id}'] button")?.TextContent);

        var ajax = await JsonAsync(await managerWeb.PostAsync("/Manager/Groups/InviteAjax", InviteForm(group.Id, invitee)), HttpStatusCode.BadRequest);
        Assert.Equal("A pending invitation already exists for this user in the specified group", ajax.GetProperty("message").GetString());

        // The initiating POST is English. The same refusal is rendered by the Greek destination.
        managerWeb.DefaultRequestHeaders.AcceptLanguage.Clear();
        using var redirect = await managerWeb.PostAsync("/Manager/Groups/Invite", InviteForm(group.Id, invitee));
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.NotNull(redirect.Headers.Location);
        var tempData = redirect.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider=", StringComparison.Ordinal)).Split(';')[0];
        var cookies = managerWeb.DefaultRequestHeaders.GetValues("Cookie").Single();
        managerWeb.DefaultRequestHeaders.Remove("Cookie");
        managerWeb.DefaultRequestHeaders.Add("Cookie", $"{cookies}; {tempData}");
        managerWeb.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        var destination = await GroupDocumentAsync(await managerWeb.GetAsync(redirect.Headers.Location));
        Assert.Equal("Ο χρήστης έχει ήδη εκκρεμή πρόσκληση για την ομάδα.",
            destination.QuerySelector(".site-content [role=alert] > span")?.TextContent);
    }

    /// <summary>Reads the compiled Razor response at the authorized MVC route.</summary>
    private static async Task<IDocument> GroupDocumentAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.EnsureSuccessStatusCode();
            return await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        }
    }
}
