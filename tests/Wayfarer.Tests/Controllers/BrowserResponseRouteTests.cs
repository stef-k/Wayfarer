using Microsoft.AspNetCore.TestHost;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Tests.Middleware;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Executes production routes, visibility checks and Razor results for the framing boundary.</summary>
public sealed class BrowserResponseRouteTests : TestBase
{
    /// <summary>Only the two eligible public embed render paths receive external framing permission.</summary>
    [Fact]
    public async Task PublicAndOrdinaryRoutes_UseFinalPolicy()
    {
        var db = CreateDbContext();
        var owner = new ApplicationUser { UserName = "public-owner", DisplayName = "Public Owner",
            IsActive = true, IsTimelinePublic = true, PublicTimelineTimeThreshold = "now" };
        var privateOwner = new ApplicationUser { UserName = "private-owner", DisplayName = "Private Owner",
            IsActive = true, IsTimelinePublic = false };
        var trip = new Trip { Id = Guid.NewGuid(), User = owner, UserId = owner.Id, Name = "Public trip", IsPublic = true };
        var privateTrip = new Trip { Id = Guid.NewGuid(), User = privateOwner, UserId = privateOwner.Id, Name = "Private trip" };
        db.Users.AddRange(owner, privateOwner);
        db.Trips.AddRange(trip, privateTrip);
        db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        var cases = new (string Path, int Status, bool Embed)[]
        {
            ("/Identity/Account/Login", 200, false),
            ("/Admin/Users", 302, false),
            ($"/Public/Trips/{trip.Id}", 200, false),
            ($"/Public/Trips/{trip.Id}?embed=true", 200, true),
            ("/Public/Users/Timeline/public-owner", 200, false),
            ("/Public/Users/Timeline/public-owner/embed", 200, true),
            ($"/Public/Trips/{privateTrip.Id}?embed=true", 404, false),
            ($"/Public/Trips/{Guid.NewGuid()}?embed=true", 404, false),
            ("/Public/Users/Timeline/private-owner/embed", 404, false),
            ("/Public/Users/Timeline/missing/embed", 404, false),
            ("/missing-page", 404, false)
        };
        foreach (var (path, status, embed) in cases)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(status, (int)response.StatusCode);
            BrowserResponsePolicyTests.AssertHeaders(response, embed);
        }
    }
}
