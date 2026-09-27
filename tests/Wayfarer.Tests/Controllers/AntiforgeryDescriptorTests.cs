using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Guard the classified production mutation inventory, including inherited controller filters.</summary>
public sealed class AntiforgeryDescriptorTests : TestBase
{
    // Explicit cookie contracts, not a blanket rule that treats read-only POSTs as mutations.
    private const string CookieActions = """
        Admin.ActivityType.Create Admin.ActivityType.Edit Admin.ActivityType.DeleteConfirmed
        Admin.ApiToken.Create Admin.ApiToken.Regenerate Admin.ApiToken.Delete
        Admin.Jobs.StartJob Admin.Jobs.PauseJob Admin.Jobs.ResumeJob Admin.Jobs.CancelJob
        Admin.Settings.Update Admin.Settings.DeleteAllMapTileCache Admin.Settings.DeleteLruCache
        Admin.Settings.ClearMbtilesCache Admin.Settings.UseRecommendedTileOutboundBudget
        Admin.Settings.AcknowledgeHistoricalTileOutboundBudget
        Admin.TransportProfile.Create Admin.TransportProfile.Edit Admin.TransportProfile.DeleteConfirmed
        Admin.Users.ChangePassword Admin.Users.Create Admin.Users.DeleteConfirmed Admin.Users.Edit
        Manager.ApiToken.Create Manager.ApiToken.Regenerate Manager.ApiToken.Delete
        Manager.Groups.Invite Manager.Groups.RemoveMember Manager.Groups.RevokeInvite Manager.Groups.ConfirmDelete
        Manager.Groups.InviteAjax Manager.Groups.RemoveMemberAjax Manager.Groups.RevokeInviteAjax
        Manager.Groups.Create Manager.Groups.Edit Manager.Users.Create Manager.Users.DeleteConfirmed
        Manager.Users.Edit Manager.Users.ChangePassword
        User.Timeline.UpdateSettings User.Groups.Create User.Groups.InviteAjax User.Groups.RemoveMemberAjax
        User.Groups.RevokeInviteAjax User.HiddenAreas.Create User.HiddenAreas.Edit User.HiddenAreas.DeleteConfirmed
        User.Location.Create User.Location.Edit User.Location.BulkEditNotes
        User.LocationImport.StartEnrichment User.LocationImport.PauseEnrichment User.LocationImport.ResumeEnrichment
        User.LocationImport.CancelEnrichment User.LocationImport.RetryDeferredEnrichment
        User.LocationImport.RepairIncompleteAddresses User.LocationImport.StartImport User.LocationImport.StopImport
        User.LocationImport.Delete User.LocationImport.Upload
        User.LocationProviderSettings.MigrateLegacyMapbox
        User.LocationProviderSettings.SaveProfile User.LocationProviderSettings.SaveCredential
        User.LocationProviderSettings.ChooseProvider User.LocationProviderSettings.ConsentMapboxPermanent
        User.LocationProviderSettings.VerifyMapboxPermanent User.LocationProviderSettings.VerifyGeoapify
        User.LocationProviderSettings.BackfillGeoapify User.LocationProviderSettings.Revoke User.LocationProviderSettings.SaveGuard
        User.Trip.Create User.Trip.Delete User.Trip.ToggleShareProgress User.Trip.Clone User.TripImport.Import
        User.TripTags.Attach User.TripTags.Remove User.Visit.Edit User.Visit.Delete
        Api.Backfill.Apply Api.Backfill.Clear Api.ExternalRouteProposals.Generate Api.Groups.Create
        Api.Groups.ToggleOrgPeerVisibility Api.Groups.SetMemberOrgPeerVisibilityAccess Api.Groups.Leave Api.Groups.RemoveMember
        Api.Invitations.Create Api.Invitations.Accept Api.Invitations.Decline Api.Location.BulkDelete
        Api.TripEditor.CreateArea Api.TripEditor.UpdateArea Api.TripEditor.UpdateAreaGeometry Api.TripEditor.DeleteArea
        Api.TripEditor.OrderAreas Api.TripEditor.PatchMetadata Api.TripEditor.CreateRegion Api.TripEditor.UpdateRegion
        Api.TripEditor.DeleteRegion Api.TripEditor.OrderRegions Api.TripEditor.SearchGeocode Api.TripEditor.CreatePlace
        Api.TripEditor.UpdatePlace Api.TripEditor.DeletePlace Api.TripEditor.OrderPlaces Api.TripEditor.CreateSegment
        Api.TripEditor.UpdateSegment Api.TripEditor.DeleteSegment Api.TripEditor.OrderSegments Api.TripEditor.PutTags
        Api.TripEditor.PatchShareProgress Api.Users.DeleteAllUserLocations Api.Visit.BulkDelete
        """;

    // These routes have their own credential authority; cookie tokens must not become a mobile requirement.
    private const string BearerActions = """
        Api.Location.CheckIn Api.Location.LogLocation Api.Location.Delete Api.MobileGroups.Latest Api.MobileGroups.Query
        Api.MobileGroups.SetPeerVisibility Api.MobileRouting.Route Api.Trips.CreatePlace Api.Trips.UpdatePlace
        Api.Trips.DeletePlace Api.Trips.CreateRegion Api.Trips.UpdateTrip Api.Trips.UpdateSegmentNotes
        Api.Trips.UpdateRegion Api.Trips.UpdateArea Api.Trips.DeleteRegion Api.Trips.CloneTrip
        """;

    // Unsafe verbs used only for queries remain deliberate, enumerated exceptions.
    private const string QueryActions = """
        Api.Groups.Latest Api.Groups.Query Api.Location.GetUserLocations Api.Visit.GetLocationCounts
        Public.UsersTimeline.GetPublicTimeline
        """;

    /// <summary>Inspect production descriptors rather than reflecting only direct method attributes.</summary>
    [Fact]
    public async Task ProductionMutationContracts_HaveOnlyTheirIntendedValidation()
    {
        await using var app = await IdentityRouteHost.StartAsync(CreateDbContext(), CreateTestDirectory());
        var all = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().ToArray();
        var unsafeActions = all.Where(action => Methods(action).Any(method => method is "POST" or "PUT" or "PATCH" or "DELETE")).ToArray();
        var classified = (CookieActions + " " + BearerActions + " " + QueryActions + " Api.Location.Update")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        Assert.All(unsafeActions, action => Assert.Contains(Key(action), classified));
        foreach (var key in CookieActions.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = unsafeActions.Where(action => Key(action) == key).ToArray();
            Assert.NotEmpty(matches);
            Assert.All(matches, action =>
            {
                Assert.Contains(action.FilterDescriptors, filter => filter.Filter is ValidateAntiForgeryTokenAttribute
                    or AutoValidateAntiforgeryTokenAttribute);
                Assert.DoesNotContain(action.FilterDescriptors, filter => filter.Filter is IgnoreAntiforgeryTokenAttribute);
            });
        }
        var admins = unsafeActions.Where(action => action.RouteValues["area"] == "Admin").ToArray();
        Assert.Equal(23, admins.Length);
        Assert.All(admins, action => Assert.Contains(action.FilterDescriptors,
            filter => filter.Filter is ValidateAntiForgeryTokenAttribute));
        foreach (var key in BearerActions.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = all.Where(action => Key(action) == key).ToArray();
            Assert.NotEmpty(matches);
            Assert.All(matches, NoValidation);
        }
        var mixed = Assert.Single(all, action => action.FilterDescriptors.Any(filter => filter.Filter is CookieFirstAntiforgeryAttribute));
        Assert.Equal("Api.Location.Update", Key(mixed));
        Assert.Equal(["PUT"], Methods(mixed));
        Assert.Equal("api/Location/{id:int}", mixed.AttributeRouteInfo!.Template);
        Assert.DoesNotContain(mixed.FilterDescriptors, filter => filter.Filter is IgnoreAntiforgeryTokenAttribute
            or ValidateAntiForgeryTokenAttribute or AutoValidateAntiforgeryTokenAttribute);
        // Deliberate read-only queries, including provider settings navigation.
        foreach (var key in (QueryActions + " User.LocationProviderSettings.Index")
                     .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = all.Where(action => Key(action) == key).ToArray();
            Assert.NotEmpty(matches);
            Assert.All(matches, NoValidation);
        }
    }

    /// <summary>Conventional settings routes separate User-authorized GET navigation from the protected command.</summary>
    [Fact]
    public async Task ProviderSettings_SeparatesReadAndCommandDescriptors()
    {
        await using var app = await IdentityRouteHost.StartAsync(CreateDbContext(), CreateTestDirectory());
        var all = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().ToArray();
        var index = Assert.Single(all, action => Key(action) == "User.LocationProviderSettings.Index");
        var command = Assert.Single(all, action => Key(action) == "User.LocationProviderSettings.MigrateLegacyMapbox");
        Assert.Equal(["GET"], Methods(index));
        Assert.Equal(["POST"], Methods(command));
        foreach (var action in new[] { index, command })
        {
            Assert.Null(action.AttributeRouteInfo);
            Assert.Contains(action.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>(),
                metadata => metadata.Roles == "User");
            Assert.DoesNotContain(action.EndpointMetadata, metadata => metadata is Microsoft.AspNetCore.Authorization.IAllowAnonymous);
        }
        Assert.Single(command.Parameters);
        Assert.Equal(typeof(CancellationToken), command.Parameters[0].ParameterType);
    }

    /// <summary>The intended mobile clone POST survives while the accidental unconstrained alias disappears.</summary>
    [Fact]
    public async Task Clone_HasOnlyItsIntendedPostRoute()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "source");
        var caller = TestDataFixtures.CreateUser(id: "mobile");
        var id = Guid.NewGuid();
        db.Users.AddRange(owner, caller);
        db.ApiTokens.Add(new ApiToken { User = caller, UserId = caller.Id, Name = "mobile", Token = "phone" });
        db.Trips.Add(new Trip { Id = id, User = owner, UserId = owner.Id, Name = "Shared trip", IsPublic = true });
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(),
            services => services.AddSingleton(Mock.Of<ICacheWarmupScheduler>()));
        var all = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>();
        var clone = Assert.Single(all, action => Key(action) == "Api.Trips.CloneTrip");
        Assert.Equal("api/Trips/{id}/clone", clone.AttributeRouteInfo!.Template);
        Assert.Equal(["POST"], Methods(clone));
        using var client = app.GetTestClient();
        using var intended = await client.PostAsync($"/api/trips/{id}/clone", null);
        Assert.Equal(HttpStatusCode.Unauthorized, intended.StatusCode);
        Assert.Equal("Missing or invalid API token.", await intended.Content.ReadAsStringAsync());
        using var get = await client.GetAsync($"/api/trips/{id}/clone");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var alias = await client.GetAsync($"/api/Trips/api/trips/{id}/clone");
        Assert.Equal(HttpStatusCode.NotFound, alias.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "phone");
        using var cloned = await client.PostAsync($"/api/trips/{id}/clone", null);
        Assert.Equal(HttpStatusCode.OK, cloned.StatusCode);
        Assert.Contains("clonedTripId", await cloned.Content.ReadAsStringAsync());
        Assert.Single(db.Trips, trip => trip.UserId == caller.Id && !trip.IsPublic);
    }

    /// <summary>Use method identity because MVC ActionName can intentionally differ on confirmation forms.</summary>
    private static string Key(ControllerActionDescriptor action) =>
        $"{action.RouteValues["area"]}.{action.ControllerName}.{action.MethodInfo.Name}";

    /// <summary>Read effective selector constraints, including the unconstrained-selector case.</summary>
    private static string[] Methods(ControllerActionDescriptor action) => action.ActionConstraints?
        .OfType<HttpMethodActionConstraint>().SelectMany(constraint => constraint.HttpMethods).ToArray() ?? [];

    /// <summary>Bearer/read exclusions must not gain either unconditional or conditional cookie validation.</summary>
    private static void NoValidation(ControllerActionDescriptor action) => Assert.DoesNotContain(action.FilterDescriptors,
        filter => filter.Filter is ValidateAntiForgeryTokenAttribute or AutoValidateAntiforgeryTokenAttribute
            or CookieFirstAntiforgeryAttribute);
}
