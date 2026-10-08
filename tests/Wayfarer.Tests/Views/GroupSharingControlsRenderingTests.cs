using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using UserGroupsController = Wayfarer.Areas.User.Controllers.GroupsController;

namespace Wayfarer.Tests.Views;

/// <summary>Proves the User-account management surface through its controller and compiled shared Razor controls.</summary>
public sealed class GroupSharingControlsRenderingTests : TestBase
{
    /// <summary>Active delegated managers can find the switch; members see policy and only an effective personal control.</summary>
    [Theory]
    [InlineData("Organization", false, "Owner", true, false)]
    [InlineData("Organization", true, "Manager", true, true)]
    [InlineData("Organization", false, "Member", false, false)]
    [InlineData("Organization", true, "Member", false, true)]
    [InlineData("Friends", false, "Member", false, true)]
    [InlineData("Family", false, "Owner", false, false)]
    public async Task UserMapRendersAuthorizedPersistedControls(string type, bool enabled, string role, bool manages, bool personal)
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "user");
        db.Users.Add(user);
        var group = new Group { Id = Guid.NewGuid(), Name = "Shared", OwnerUserId = "other-owner", GroupType = type, OrgPeerVisibilityEnabled = enabled };
        var member = new GroupMember { GroupId = group.Id, UserId = user.Id, Role = role, OrgPeerVisibilityAccessDisabled = true };
        db.Groups.Add(group);
        db.GroupMembers.Add(member);
        await db.SaveChangesAsync();
        var controller = new UserGroupsController(NullLogger<BaseController>.Instance, db)
            { ControllerContext = new ControllerContext { HttpContext = BuildHttpContextWithUser(user.Id, "User") } };
        var result = Assert.IsType<ViewResult>(await controller.Map(group.Id));
        Assert.Equal(member.Id, Assert.IsType<GroupMember>(result.ViewData["MyMembership"]).Id);
        using var host = Host.CreateDefaultBuilder().ConfigureWebHostDefaults(web => web
            .UseSetting(WebHostDefaults.ApplicationKey, typeof(Group).Assembly.GetName().Name)
            .ConfigureServices(services => services.AddControllersWithViews().AddApplicationPart(typeof(Group).Assembly))
            .Configure(_ => { })).Build();
        using var scope = host.Services.CreateScope();
        var html = await RenderAsync(scope.ServiceProvider, group, member);
        Assert.Equal(manages, html.Contains("id=\"orgPeerVisibilityToggle\"", StringComparison.Ordinal));
        Assert.Equal(personal, html.Contains("id=\"peerVisibilityToggle\"", StringComparison.Ordinal));
        if (type == "Organization") Assert.Contains(enabled ? "Sharing is on" : "Sharing is off", html);
        // The stored opt-out stays unchecked even when the Group allows willing peers.
        if (personal) Assert.DoesNotContain("id=\"peerVisibilityToggle\" checked=", html);
    }

    /// <summary>Renders the production partial, without a browser host or a reimplemented authorization template.</summary>
    private static async Task<string> RenderAsync(IServiceProvider services, Group group, GroupMember member)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var view = services.GetRequiredService<ICompositeViewEngine>().GetView(null, "/Views/Shared/_GroupSharingControls.cshtml", false);
        Assert.True(view.Success);
        var data = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = (group, member) };
        var temp = new TempDataDictionary(http, services.GetRequiredService<ITempDataProvider>());
        await using var writer = new StringWriter();
        await view.View.RenderAsync(new ViewContext(action, view.View, data, temp, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }
}
