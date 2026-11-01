using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Services;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Production routed cookie/bearer adapters plus one shared exact-Group/public-context policy matrix.</summary>
[Collection("API token singleton admission")]
public sealed class ProfilePhotoHttpTests : TestBase
{
    /// <summary>Credentials never enter URLs; conditional requests still recheck persisted authority.</summary>
    [Fact]
    public async Task CookieBearerAndPublicReadsAuthorizeBeforeOpeningAndNeverCache()
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(id: "photo-owner");
        var caller = TestDataFixtures.CreateUser(id: "photo-caller");
        var version = Guid.NewGuid(); owner.ProfilePhotoVersion = version; owner.ShowProfilePhotoOnPublicTrips = true;
        var group = new Group { Id = Guid.NewGuid(), Name = "Photo group", OwnerUserId = owner.Id };
        var trip = new Trip { Id = Guid.NewGuid(), Name = "Photo trip", User = owner, UserId = owner.Id, IsPublic = true };
        db.AddRange(owner, caller, group, trip, new ApplicationSettings(),
            new GroupMember { GroupId = group.Id, UserId = owner.Id, OrgPeerVisibilityAccessDisabled = true },
            new GroupMember { GroupId = group.Id, UserId = caller.Id },
            new ApiToken { User = caller, UserId = caller.Id, Name = "phone", Token = "photo-test-token" });
        await db.SaveChangesAsync();
        using var files = new PhotoTestStorage();
        await files.Store.PublishAsync(version, "photo"u8.ToArray(), default);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(files.Store.Root))!;
        await using var app = await IdentityRouteHost.StartAsync(db, root, services => services.AddSingleton(files.Store));
        using var client = app.GetTestClient();
        var groupPath = $"/api/groups/{group.Id}/members/{owner.Id}/photo/{version:N}";
        using (var anonymous = await client.GetAsync(groupPath)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, caller.Id));
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", "*");
        using (var cookie = await client.GetAsync(groupPath)) AssertPhoto(cookie, HttpStatusCode.OK);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer photo-test-token");
        using (var bearer = await client.GetAsync(groupPath.Replace("/api/groups/", "/api/mobile/groups/"))) AssertPhoto(bearer, HttpStatusCode.OK);
        using (var ownerDenied = await client.GetAsync($"/api/profile-photo/me/{version:N}")) AssertPhoto(ownerDenied, HttpStatusCode.Unauthorized);
        var publicPath = $"/Public/Trips/{trip.Id}/owner-photo/{version:N}";
        using (var visible = await client.GetAsync(publicPath)) AssertPhoto(visible, HttpStatusCode.OK);
        owner.ShowProfilePhotoOnPublicTrips = false; await db.SaveChangesAsync();
        using (var optedOut = await client.GetAsync(publicPath)) AssertPhoto(optedOut, HttpStatusCode.NotFound);
        owner.ShowProfilePhotoOnPublicTrips = true; trip.IsPublic = false; await db.SaveChangesAsync();
        using (var privateTrip = await client.GetAsync(publicPath)) AssertPhoto(privateTrip, HttpStatusCode.NotFound);
        db.ApiTokens.Remove(await db.ApiTokens.SingleAsync()); await db.SaveChangesAsync();
        using var revoked = await client.GetAsync(groupPath.Replace("/api/groups/", "/api/mobile/groups/"));
        AssertPhoto(revoked, HttpStatusCode.Unauthorized);
    }

    /// <summary>Shared policy rejects stale/other contexts without any role or location-audience bypass.</summary>
    [Theory]
    [InlineData("caller-left")]
    [InlineData("target-pending")]
    [InlineData("archived")]
    [InlineData("inactive")]
    [InlineData("stale")]
    [InlineData("other-group")]
    public async Task GroupPolicyRejectsInvalidPersistedContext(string denial)
    {
        var db = CreateDbContext();
        var owner = TestDataFixtures.CreateUser(); var caller = TestDataFixtures.CreateUser();
        var version = Guid.NewGuid(); owner.ProfilePhotoVersion = version;
        var group = new Group { Id = Guid.NewGuid(), Name = "Group", OwnerUserId = owner.Id };
        var member = new GroupMember { GroupId = group.Id, UserId = caller.Id, Role = GroupMember.Roles.Owner };
        var target = new GroupMember { GroupId = group.Id, UserId = owner.Id, OrgPeerVisibilityAccessDisabled = true };
        db.AddRange(owner, caller, group, member, target); await db.SaveChangesAsync();
        using var storage = new PhotoTestStorage();
        using var provider = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var service = new ProfilePhotoService(provider.GetRequiredService<IServiceScopeFactory>(), storage.Store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProfilePhotoService>.Instance);
        Assert.Equal(version, await service.GroupVersionAsync(caller.Id, group.Id, owner.Id, version, default));
        switch (denial)
        {
            case "caller-left": member.Status = GroupMember.MembershipStatuses.Left; break;
            case "target-pending": target.Status = "Pending"; break;
            case "archived": group.IsArchived = true; break;
            case "inactive": owner.IsActive = false; break;
            case "stale": version = Guid.NewGuid(); break;
            case "other-group": group = new Group { Id = Guid.NewGuid(), Name = "Other", OwnerUserId = caller.Id }; db.Add(group); break;
        }
        await db.SaveChangesAsync();
        Assert.Null(await service.GroupVersionAsync(caller.Id, group.Id, owner.Id, version, default));
    }

    private static void AssertPhoto(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag); Assert.Null(response.Content.Headers.LastModified);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
