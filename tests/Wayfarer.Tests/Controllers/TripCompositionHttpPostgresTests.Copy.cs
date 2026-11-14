using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripCompositionHttpPostgresTests
{
    /// <summary>Cookie CSRF and incoming bearer authority protect the same atomic copy operation and public freshness effects.</summary>
    [PostgresFact]
    public async Task CookieAndBearerCopyUseSameOperationWithoutWorkspaceOrImplicitMetadata()
    {
        var owner = await fixture.CreateUserAsync(); var foreign = await fixture.CreateUserAsync();
        var destination = Source(owner.Id, "Existing destination", true, "CC0-1.0", 23);
        var source = Source(foreign.Id, "Reusable source", true, "CC-BY-4.0", 24);
        fixture.RegisterTrip(source.Id); fixture.RegisterTrip(destination.Id);
        await using var db = fixture.CreateContext();
        db.AddRange(source, destination, new ApiToken { UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id), Token = owner.Id, Name = "copy" }); await db.SaveChangesAsync();
        var thumbnails = new Mock<ITripMapThumbnailGenerator>(); var warmup = new Mock<ICacheWarmupScheduler>();
        warmup.Setup(s => s.ScheduleWarmupAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton(thumbnails.Object); services.AddSingleton(warmup.Object); services.AddSingleton(Mock.Of<IIconColorProvider>());
        });
        using var browser = app.GetTestClient(); using var mobile = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(browser, app.Services, owner.Id);
        mobile.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        var url = $"/api/trip-compositions/copy-preview?sourceTripId={source.Id}&destinationTripId={destination.Id}";
        using var previewResponse = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode); Assert.True(previewResponse.Headers.CacheControl!.NoStore);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("source").TryGetProperty("planning", out _));
        using var wrongAuthority = await browser.GetAsync(url.Replace("trip-compositions", "trips"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAuthority.StatusCode);
        var parent = destination.Regions.Single(); var target = parent.Places.Single(); var place = source.Regions.Single().Places.Single();
        var request = new { sourceTripId = source.Id, destinationTripId = destination.Id, context = preview.GetProperty("context"), originals = preview.GetProperty("originals"),
            items = new[] { new { kind = "place", sourceId = place.Id, decision = "copy", regionId = parent.Id, name = "Copied idea",
                placement = new { expected = new[] { new { id = target.Id, displayOrder = target.DisplayOrder } }, position = "after", afterId = target.Id } } } };
        using var noCsrf = await browser.PostAsJsonAsync("/api/trip-compositions/copy", request);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var cookieOnly = await browser.PostAsJsonAsync("/api/trips/copy", request);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        using var copied = await mobile.PostAsJsonAsync("/api/trips/copy", request);
        Assert.True(copied.StatusCode == HttpStatusCode.OK, await copied.Content.ReadAsStringAsync()); Assert.True(copied.Headers.CacheControl!.NoStore);
        var result = await copied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(destination.Id, result.GetProperty("tripId").GetGuid()); Assert.Equal("copied", result.GetProperty("status").GetString());
        Assert.Equal(2, result.GetProperty("current").GetProperty("graph").GetProperty("regions")[0].GetProperty("places").GetArrayLength());
        Assert.Equal("CC0-1.0", result.GetProperty("current").GetProperty("graph").GetProperty("contributionPolicy").GetString());
        thumbnails.Verify(t => t.InvalidateThumbnails(destination.Id, It.IsAny<DateTime>()), Times.Once);
        warmup.Verify(t => t.ScheduleWarmupAsync(destination.Id), Times.Once);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(browser);
        using var newPreviewResponse = await browser.GetAsync(url);
        var newPreview = await newPreviewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var reuse = new { sourceTripId = source.Id, destinationTripId = destination.Id, context = newPreview.GetProperty("context"), originals = newPreview.GetProperty("originals"),
            items = new[] { new { kind = "place", sourceId = place.Id, decision = "reuse", destinationId = target.Id, expectedParentId = parent.Id } } };
        using var reused = await browser.PostAsJsonAsync("/api/trip-compositions/copy", reuse);
        Assert.True(reused.StatusCode == HttpStatusCode.OK, await reused.Content.ReadAsStringAsync());
        thumbnails.Verify(t => t.InvalidateThumbnails(destination.Id, It.IsAny<DateTime>()), Times.Once);
        Assert.False(await db.Set<TripComposition>().AnyAsync(w => w.TripId == destination.Id));
        Assert.Single(await db.Places.Where(p => p.Region.TripId == source.Id).ToListAsync());
        await db.ApiTokens.Where(t => t.UserId == owner.Id).ExecuteDeleteAsync();
        using var revoked = await mobile.PostAsJsonAsync("/api/trips/copy", reuse); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    /// <summary>Real bearer/native transport preserves complete portable content, exact measurements and explicit owner planning.</summary>
    [PostgresFact]
    public async Task BearerNativeDownloadCreateAndReviewedReplacementShareV5Pipeline()
    {
        var owner = await fixture.CreateUserAsync();
        var source = Source(owner.Id, "Native transport", false, null, 24);
        source.Planning = new(1, [new(Guid.NewGuid(), new("unknown"), "owner planning")]);
        source.Credits = [new("Prior author", "Offline ideas", "Other supplied terms", Notices: "Keep notice")];
        var place = source.Regions.Single().Places.Single();
        var route = new Segment { Id = Guid.NewGuid(), TripId = source.Id, UserId = owner.Id, Title = "Optional title", Notes = "<p>Route Notes</p>", Mode = "walk",
            FromPlaceId = place.Id, ToPlaceId = null, EstimatedDurationSource = EstimatedDurationSource.Manual, EstimatedDuration = TimeSpan.FromTicks(12345670), EstimatedDistanceKm = 1.23456789 };
        source.Segments.Add(route); fixture.RegisterTrip(source.Id);
        await using var db = fixture.CreateContext(); db.AddRange(source, new ApiToken { UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id), Token = owner.Id, Name = "native" }); await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext()); services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture)); services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>()); services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });
        using var cookie = app.GetTestClient(); UserConnectionTokenHttpTests.Cookie(cookie, app.Services, owner.Id);
        using var denied = await cookie.GetAsync($"/api/trips/{source.Id}/native?includePrivatePlanning=true"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var mobile = app.GetTestClient(); mobile.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        using var defaultDownload = await mobile.GetAsync($"/api/trips/{source.Id}/native");
        Assert.True(defaultDownload.StatusCode == HttpStatusCode.OK, await defaultDownload.Content.ReadAsStringAsync()); Assert.True(defaultDownload.Headers.CacheControl!.NoStore);
        var defaultXml = await defaultDownload.Content.ReadAsStringAsync(); Assert.DoesNotContain("Planning", defaultXml);
        using var personal = await mobile.GetAsync($"/api/trips/{source.Id}/native?includePrivatePlanning=true");
        Assert.Equal(HttpStatusCode.OK, personal.StatusCode); var xml = await personal.Content.ReadAsStringAsync();
        Assert.Contains("Planning", xml); Assert.Contains("Optional title", xml); Assert.Contains("Keep notice", xml);
        using var imported = await mobile.PostAsync("/api/trips/native/import", Upload(xml, "CreateNew"));
        Assert.True(imported.StatusCode == HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        var result = await imported.Content.ReadFromJsonAsync<JsonElement>(); var id = result.GetProperty("tripId").GetGuid(); fixture.RegisterTrip(id);
        Assert.NotEqual(source.Id, id);
        var saved = await db.Trips.AsNoTracking().Include(t => t.Regions).ThenInclude(r => r.Places).Include(t => t.Segments).SingleAsync(t => t.Id == id);
        Assert.False(saved.IsPublic); Assert.Null(saved.ContributionPolicy); Assert.Equal(source.Credits, saved.Credits);
        Assert.NotEqual(source.Planning!.Entries[0].Id, saved.Planning!.Entries[0].Id);
        var copied = saved.Segments.Single(); Assert.Equal(route.Title, copied.Title); Assert.Equal(route.Notes, copied.Notes);
        Assert.Equal(route.EstimatedDuration, copied.EstimatedDuration); Assert.Equal(route.EstimatedDistanceKm, copied.EstimatedDistanceKm);
        Assert.Null(copied.ToPlaceId); Assert.NotEqual(place.Id, copied.FromPlaceId);
        using var review = await mobile.PostAsync("/api/trips/native/review", Upload(xml, "Upsert"));
        Assert.True(review.StatusCode == HttpStatusCode.OK, await review.Content.ReadAsStringAsync());
        var reviewed = await review.Content.ReadFromJsonAsync<JsonElement>();
        var replacement = Upload(xml, "Upsert"); replacement.Add(new StringContent(reviewed.GetProperty("review").GetProperty("token").GetString()!), "reviewToken");
        using var replaced = await mobile.PostAsync("/api/trips/native/import", replacement);
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync());
        using var malformed = await mobile.PostAsync("/api/trips/native/import", Upload("<kml><Document><name>Generic</name></Document></kml>", "CreateNew"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        using var ownerRead = await mobile.GetAsync($"/api/trips/{source.Id}/planning"); Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
    }

    private static MultipartFormDataContent Upload(string xml, string mode)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent(xml, Encoding.UTF8, "application/vnd.google-earth.kml+xml"), "file", "trip.kml");
        body.Add(new StringContent(mode), "mode"); return body;
    }
}
