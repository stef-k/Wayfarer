using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Controllers;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Models;

/// <summary>Proves the incidental Trip timestamp write footprint against independent PostgreSQL writers.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripTimestampPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output) : TestBase
{
    /// <summary>A routed Region update or an untracked-root Place insert preserves a later Web metadata edit.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChildSave_PreservesConcurrentWebMetadata(bool routedRegionUpdate)
    {
        var region = await SeedAsync();
        var gate = new StampGate();
        await using var db = fixture.CreateContext(gate);
        await using var app = await StartAsync(db, gate);
        using var web = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(web, app.Services, region.UserId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(web);
        using var bearer = app.GetTestClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", region.UserId);
        var place = new Place { Id = Guid.NewGuid(), RegionId = region.Id, UserId = region.UserId, Name = "New child" };
        if (!routedRegionUpdate) db.Places.Add(place);
        Assert.Empty(db.ChangeTracker.Entries<Trip>());

        Task pending = routedRegionUpdate
            ? UpdateRegionAsync(bearer, region.Id)
            : db.SaveChangesAsync();
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var expected = await EditWebMetadataAsync(web, region.TripId);
            gate.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(15));

            await using var verification = fixture.CreateContext();
            var stored = await verification.Trips.SingleAsync(trip => trip.Id == region.TripId);
            output.WriteLine("Trip dirty properties: " + string.Join(", ", gate.DirtyProperties));
            output.WriteLine("Trip save SQL: " + gate.TripUpdateSql);
            Assert.Equal(expected, Metadata(stored));
            Assert.Equal(gate.Timestamp.Ticks / 10, stored.UpdatedAt.Ticks / 10);
            Assert.Equal([nameof(Trip.UpdatedAt)], gate.DirtyProperties);
            Assert.Contains("UPDATE \"Trips\" SET \"UpdatedAt\" =", gate.TripUpdateSql);
            Assert.DoesNotContain("\"Name\" =", gate.TripUpdateSql);
            if (routedRegionUpdate)
                Assert.Equal("Changed child", (await verification.Regions.FindAsync(region.Id))!.Notes);
            else
                Assert.True(await verification.Places.AnyAsync(item => item.Id == place.Id));
            Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        }
        finally
        {
            gate.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    /// <summary>Adding the incidental stamp must keep the request's intentional root property dirty.</summary>
    [PostgresFact]
    public async Task ChildSave_PreservesIntentionalRootEditWithoutExpandingItsFootprint()
    {
        var region = await SeedAsync();
        var gate = new StampGate();
        await using var db = fixture.CreateContext(gate);
        var trip = await db.Trips.SingleAsync(item => item.Id == region.TripId);
        var child = await db.Regions.SingleAsync(item => item.Id == region.Id);
        trip.Notes = "Intentional root edit";
        child.Notes = "Changed child";
        await using var app = await StartAsync(db, gate);
        using var web = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(web, app.Services, region.UserId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(web);
        var pending = db.SaveChangesAsync();
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var expected = await EditWebMetadataAsync(web, trip.Id);
            gate.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(15));

            await using var verification = fixture.CreateContext();
            Assert.Equal(expected with { Notes = trip.Notes }, Metadata(await verification.Trips.SingleAsync(item => item.Id == trip.Id)));
            Assert.Equal([nameof(Trip.Notes), nameof(Trip.UpdatedAt)], gate.DirtyProperties);
            Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        }
        finally
        {
            gate.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    /// <summary>A failure immediately before commit rolls back the child and timestamp together.</summary>
    [PostgresFact]
    public async Task FailedCommit_RollsBackChildAndTimestamp()
    {
        var region = await SeedAsync();
        var failure = new FailCommit();
        await using var db = fixture.CreateContext(failure);
        var trip = await db.Trips.SingleAsync(item => item.Id == region.TripId);
        var timestamp = trip.UpdatedAt;
        var child = await db.Regions.SingleAsync(item => item.Id == region.Id);
        child.Notes = "Uncommitted child";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.Equal("Controlled pre-commit failure", exception.Message);
        Assert.True(failure.Reached);
        Assert.Equal([nameof(Trip.UpdatedAt)], db.Entry(trip).Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name));
        await using var verification = fixture.CreateContext();
        Assert.Null((await verification.Regions.FindAsync(region.Id))!.Notes);
        Assert.Equal(timestamp, (await verification.Trips.FindAsync(trip.Id))!.UpdatedAt);
        // Normal EF failure state is retained; dispose this context rather than replaying the failed unit of work.
    }

    /// <summary>Timestamp stamping leaves newly added and cascade-deleted roots in their intended states.</summary>
    [PostgresFact]
    public async Task AddedAndDeletedRoots_KeepInsertAndDeleteSemantics()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "New Trip", Notes = "Authored" };
        fixture.RegisterTrip(trip.Id);
        await using var db = fixture.CreateContext();
        db.Regions.Add(new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "New Region" });
        await db.SaveChangesAsync();
        Assert.NotEqual(default, trip.UpdatedAt);
        Assert.Equal(EntityState.Unchanged, db.Entry(trip).State);
        await using (var verification = fixture.CreateContext())
            Assert.Equal("Authored", (await verification.Trips.FindAsync(trip.Id))!.Notes);

        db.Trips.Remove(trip);
        await db.SaveChangesAsync();
        Assert.Equal(EntityState.Detached, db.Entry(trip).State);
        await using var deleted = fixture.CreateContext();
        Assert.False(await deleted.Trips.AnyAsync(item => item.Id == trip.Id));
        Assert.False(await deleted.Regions.AnyAsync(item => item.TripId == trip.Id));
    }

    /// <summary>Seeds an owned root with every metadata column distinct from the later Web draft.</summary>
    private async Task<Region> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip
        {
            Id = Guid.NewGuid(), UserId = user.Id, Name = "Before", Notes = "Before notes", IsPublic = true,
            ShareProgressEnabled = true, CoverImageUrl = "https://example.test/before.jpg", CenterLat = 1, CenterLon = 2, Zoom = 3
        };
        fixture.RegisterTrip(trip.Id);
        var region = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "Region" };
        await using var db = fixture.CreateContext();
        db.Regions.Add(region);
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, User = null!, Name = "853 fixture", TokenHash = ApiTokenService.HashToken(user.Id) });
        await db.SaveChangesAsync();
        return region;
    }

    /// <summary>Uses real production MVC/authentication with separate scoped PostgreSQL contexts per request.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, StampGate gate) => IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
    {
        services.AddHttpContextAccessor();
        services.AddScoped(provider => fixture.CreateContext(
            provider.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Method == "PUT" ? [gate] : []));
        services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
        services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
    });

    /// <summary>Exercises the actual bearer child action, including its pretracked unchanged Trip.</summary>
    private static async Task UpdateRegionAsync(HttpClient bearer, Guid regionId)
    {
        using var response = await bearer.PutAsJsonAsync($"/api/trips/regions/{regionId}", new { notes = "Changed child" });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Commits the actual cookie/antiforgery complete Web draft, then reads its persisted values.</summary>
    private async Task<RootMetadata> EditWebMetadataAsync(HttpClient web, Guid tripId)
    {
        using var response = await web.PatchAsJsonAsync($"/api/trips/{tripId}/editor/metadata", new
        {
            name = "Web title", notesHtml = "Web notes", isPublic = false,
            coverImage = new { rawUrl = "https://example.test/after.jpg" },
            center = new { latitude = 37.5, longitude = 23.5 }, zoom = 11
        });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var db = fixture.CreateContext();
        var stored = await db.Trips.SingleAsync(trip => trip.Id == tripId);
        Assert.Equal(new RootMetadata(stored.UserId, "Web title", "Web notes", false, false,
            "https://example.test/after.jpg", 37.5, 23.5, 11), Metadata(stored));
        return Metadata(stored);
    }

    /// <summary>Compares every root scalar except the independently stamped advisory timestamp and identity.</summary>
    private static RootMetadata Metadata(Trip trip) => new(trip.UserId, trip.Name, trip.Notes, trip.IsPublic,
        trip.ShareProgressEnabled, trip.CoverImageUrl, trip.CenterLat, trip.CenterLon, trip.Zoom);

    /// <summary>Persisted Web metadata whose values must survive an incidental child save.</summary>
    private sealed record RootMetadata(string UserId, string Name, string? Notes, bool IsPublic, bool ShareProgress,
        string? Cover, double? Latitude, double? Longitude, int? Zoom);

    /// <summary>Pauses once after stamping/loading but before any child/root DML, without modifying SQL or results.</summary>
    private sealed class StampGate : DbCommandInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string[] DirtyProperties { get; private set; } = [];
        internal DateTime Timestamp { get; private set; }
        internal string TripUpdateSql { get; private set; } = string.Empty;

        /// <summary>Captures the actual save columns and root dirty flags before admitting the independent Web commit.</summary>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && !command.CommandText.StartsWith("INSERT", StringComparison.Ordinal)) return result;
            if (command.CommandText.Contains("UPDATE \"Trips\"", StringComparison.Ordinal)) TripUpdateSql = command.CommandText;
            if (Reached.Task.IsCompleted) return result;
            var trip = eventData.Context!.ChangeTracker.Entries<Trip>().Single();
            DirtyProperties = trip.Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name).Order().ToArray();
            Timestamp = trip.Entity.UpdatedAt;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    /// <summary>Fails after EF has executed both writes in its implicit transaction, before either can commit.</summary>
    private sealed class FailCommit : DbTransactionInterceptor
    {
        internal bool Reached { get; private set; }

        /// <summary>Retains an ordinary provider failure for the caller while EF rolls back the unit of work.</summary>
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Reached = true;
            throw new InvalidOperationException("Controlled pre-commit failure");
        }
    }
}
