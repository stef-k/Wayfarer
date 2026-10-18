using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Configuration;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises real routed concurrent Place creation and bounded fallback reconciliation.</summary>
public sealed class ApiCanonicalRegionPostgresTests(PostgresMigrationTestFixture fixture)
    : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Both requests see the missing fallback before the winning save; the loser inserts only its Place.</summary>
    [PostgresTheory]
    [InlineData(null, 8)]
    [InlineData(23, 23)]
    public async Task ConcurrentRequestsConvergeWithOneFreshContext(int? loserOrder, int expectedOrder)
    {
        var trip = await SeedAsync();
        var barrier = new CompetingSaves();
        var contexts = new ConcurrentBag<ApplicationDbContext>();
        await using var hostContext = fixture.CreateContext();
        await using var app = await IdentityRouteHost.StartAsync(hostContext, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ =>
            {
                var context = fixture.CreateContext(barrier);
                contexts.Add(context);
                return context;
            });
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", trip.UserId);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/trips/{trip.Id}/places", new { name = "Winner", displayOrder = 7 }),
            client.PostAsJsonAsync($"/api/trips/{trip.Id}/places", new
            {
                name = "Loser", displayOrder = loserOrder, latitude = 37, longitude = 23,
                notes = "<p>Retried</p>", iconName = "museum", markerColor = "bg-red"
            })).WaitAsync(TimeSpan.FromSeconds(30));

        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(body.RootElement.GetProperty("success").GetBoolean());
            }
        }
        Assert.Equal(3, contexts.Count);
        Assert.Equal(3, barrier.Attempts);
        Assert.Equal(("23505", RegionConfiguration.UnassignedIndex), Assert.Single(barrier.Failures));
        await using var verify = fixture.CreateContext();
        var fallback = await verify.Regions.Include(region => region.Places).SingleAsync(region => region.TripId == trip.Id);
        Assert.Equal(("Unassigned Places", 0, trip.UserId), (fallback.Name, fallback.DisplayOrder, fallback.UserId));
        Assert.Equal(2, fallback.Places.Count);
        var loser = fallback.Places.Single(place => place.Name == "Loser");
        Assert.Equal((expectedOrder, "<p>Retried</p>", "museum", "bg-red"),
            (loser.DisplayOrder, loser.Notes, loser.IconName, loser.MarkerColor));
        Assert.Equal((23d, 37d, 4326), (loser.Location!.X, loser.Location.Y, loser.Location.SRID));
        Assert.All(fallback.Places, place => Assert.Equal(trip.UserId, place.UserId));
    }

    /// <summary>Sequential omission retains a historical exact fallback's identity/order and ignores near-names.</summary>
    [PostgresFact]
    public async Task SequentialReusePreservesHistoricalFallback()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var fallback = new Region
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId,
            Name = "Unassigned Places", DisplayOrder = 17
        };
        db.Places.Add(new Place
        {
            Id = Guid.NewGuid(), UserId = trip.UserId, Region = fallback, Name = "Existing", DisplayOrder = 5
        });
        db.Regions.Add(new Region
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = "unassigned places", DisplayOrder = 0
        });
        await db.SaveChangesAsync();
        var controller = Controller(db, trip.UserId);
        Assert.IsType<OkObjectResult>(await controller.CreatePlace(trip.Id, new() { Name = "Appended" }));
        Assert.IsType<OkObjectResult>(await controller.CreatePlace(trip.Id, new() { Name = "Again" }));
        await using var verify = fixture.CreateContext();
        var stored = await verify.Regions.Include(region => region.Places).SingleAsync(region => region.Id == fallback.Id);
        Assert.Equal((17, "Unassigned Places"), (stored.DisplayOrder, stored.Name));
        Assert.Equal(new int?[] { 5, 6, 7 }, stored.Places.Select(place => place.DisplayOrder).Order());
        Assert.Equal(2, await verify.Regions.CountAsync(region => region.TripId == trip.Id));
    }

    /// <summary>Recovery stops on an absent or incorrectly owned winner instead of recreating or hiding it.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollisionWithInvalidWinnerReturnsReconciliation(bool wrongOwner)
    {
        var trip = await SeedAsync();
        var other = await fixture.CreateUserAsync();
        var interceptor = new CollisionIntervention(async () =>
        {
            await using var competing = fixture.CreateContext();
            competing.Regions.Add(new Region
            {
                Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId,
                Name = "Unassigned Places", DisplayOrder = 17
            });
            await competing.SaveChangesAsync();
        }, async () =>
        {
            await using var repair = fixture.CreateContext();
            if (wrongOwner) await repair.Regions.Where(region => region.TripId == trip.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(region => region.UserId, other.Id));
            else await repair.Regions.Where(region => region.TripId == trip.Id).ExecuteDeleteAsync();
        });
        await using var db = fixture.CreateContext(interceptor);
        await using var services = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
        var controller = Controller(db, trip.UserId, services);

        Assert.IsType<ConflictObjectResult>(await controller.CreatePlace(trip.Id, new() { Name = "Rejected" }));

        Assert.Equal(1, interceptor.Attempts);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Places.AnyAsync(place => place.Region.TripId == trip.Id));
    }

    /// <summary>A second SQL failure is propagated after exactly two attempts, leaving the winner and no losing Place.</summary>
    [PostgresFact]
    public async Task RecoveryDoesNotRetryItsOwnFailure()
    {
        var trip = await SeedAsync();
        var attempts = 0;
        var interceptor = new CollisionIntervention(async () =>
        {
            await using var competing = fixture.CreateContext();
            competing.Regions.Add(new Region
            {
                Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId,
                Name = "Unassigned Places", DisplayOrder = 17
            });
            await competing.SaveChangesAsync();
            await competing.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_retried_place() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Controlled retry failure' USING ERRCODE = '23514'; END;
                $$;
                CREATE TRIGGER reject_retried_place BEFORE INSERT ON "Places"
                    FOR EACH ROW WHEN (NEW."Name" = 'Rejected retry') EXECUTE FUNCTION reject_retried_place();
                """);
        });
        await using var db = fixture.CreateContext(interceptor);
        db.SavingChanges += (_, _) => attempts++;
        await using var services = new ServiceCollection().AddScoped(_ =>
        {
            var context = fixture.CreateContext();
            context.SavingChanges += (_, _) => attempts++;
            return context;
        }).BuildServiceProvider();
        var controller = Controller(db, trip.UserId, services);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => controller.CreatePlace(
            trip.Id, new() { Name = "Rejected retry" }));

        Assert.Equal("23514", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(2, attempts);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Regions.CountAsync(region => region.TripId == trip.Id));
        Assert.False(await verify.Places.AnyAsync(place => place.Region.TripId == trip.Id));
    }

    /// <summary>Malformed ownership and historical ambiguity reject without timestamp or graph mutation.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedDestinationsRequireReconciliation(bool duplicates)
    {
        var trip = await SeedAsync();
        var other = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (duplicates) await db.Database.ExecuteSqlRawAsync("DROP INDEX \"UX_Regions_Trip_UnassignedPlaces\"");
        db.Regions.Add(new Region
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = duplicates ? trip.UserId : other.Id,
            Name = "Unassigned Places", DisplayOrder = 17
        });
        if (duplicates) db.Regions.Add(new Region
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = trip.UserId, Name = "Unassigned Places", DisplayOrder = 0
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.Trips.AsNoTracking().SingleAsync(item => item.Id == trip.Id);
        var saves = 0;
        db.SavingChanges += (_, _) => saves++;
        var controller = new Wayfarer.Areas.Api.Controllers.TripsController(db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Wayfarer.Areas.Api.Controllers.BaseApiController>.Instance,
            Mock.Of<ITripTagService>(), Mock.Of<IApplicationSettingsService>(), Mock.Of<ICacheWarmupScheduler>())
        {
            ControllerContext = new() { HttpContext = CreateHttpContext(trip.UserId) }
        };

        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await controller.CreatePlace(
            trip.Id, new() { Name = "Rejected" }));

        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal("unassigned_region_conflict", body.GetProperty("error").GetString());
        Assert.Equal("Unassigned Places requires reconciliation.", body.GetProperty("message").GetString());
        Assert.Equal(0, saves);
        Assert.False(await db.Places.AnyAsync(place => place.Region.TripId == trip.Id));
        Assert.Equal(before.UpdatedAt, (await db.Trips.AsNoTracking().SingleAsync(item => item.Id == trip.Id)).UpdatedAt);
        await transaction.RollbackAsync();
    }

    /// <summary>Creates only request-owned data inside the disposable fixture database.</summary>
    private async Task<Trip> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Canonical concurrency" };
        await using var db = fixture.CreateContext();
        db.Trips.Add(trip);
        db.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id, User = null!, Name = "canonical-test", TokenHash = ApiTokenService.HashToken(user.Id)
        });
        await db.SaveChangesAsync();
        return trip;
    }

    /// <summary>Supplies fresh recovery scopes while retaining the production token and action path.</summary>
    private static Wayfarer.Areas.Api.Controllers.TripsController Controller(ApplicationDbContext db, string token,
        IServiceProvider? services = null)
    {
        var http = CreateHttpContext(token);
        if (services != null) http.RequestServices = services;
        return new(db, NullLogger<Wayfarer.Areas.Api.Controllers.BaseApiController>.Instance,
            Mock.Of<ITripTagService>(), Mock.Of<IApplicationSettingsService>(), Mock.Of<ICacheWarmupScheduler>())
        {
            ControllerContext = new() { HttpContext = http }
        };
    }

    /// <summary>Commits a real contender after the action's miss and optionally changes the winner before recovery.</summary>
    private sealed class CollisionIntervention(Func<Task> beforeSave, Func<Task>? afterCollision = null) : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        /// <summary>Runs once on the original context, after the fallback is staged.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1) await beforeSave();
            return result;
        }

        /// <summary>Changes only the disposable contender after a verified index collision has rolled back.</summary>
        public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Assert.Equal(RegionConfiguration.UnassignedIndex,
                Assert.IsType<PostgresException>(eventData.Exception.InnerException).ConstraintName);
            if (afterCollision != null) await afterCollision();
        }
    }

    /// <summary>Holds both staged graphs, then commits the winner before allowing the loser's real index collision.</summary>
    private sealed class CompetingSaves : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;
        public int Attempts => _attempts;
        public ConcurrentBag<(string State, string? Index)> Failures { get; } = [];

        /// <summary>Only the two initial fallback-creating saves participate in the barrier.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            var db = eventData.Context!;
            if (!db.ChangeTracker.Entries<Region>().Any(entry => entry.State == EntityState.Added)) return result;
            if (_attempts == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (db.ChangeTracker.Entries<Place>().Single().Entity.Name == "Loser")
                await _committed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }

        /// <summary>EF has durably committed the winner before this callback.</summary>
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            _committed.TrySetResult();
            return ValueTask.FromResult(result);
        }

        /// <summary>Retains only the PostgreSQL failure identity, without database values.</summary>
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception.InnerException is PostgresException postgres)
                Failures.Add((postgres.SqlState, postgres.ConstraintName));
            return Task.CompletedTask;
        }
    }
}
