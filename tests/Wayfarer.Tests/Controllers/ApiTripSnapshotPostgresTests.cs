using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routed PostgreSQL witnesses for coherent full Trip reads and their bearer authority.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class ApiTripSnapshotPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>A committed Web move cannot mix old root metadata with new membership, position or route.</summary>
    [PostgresFact]
    public async Task WebMoveBetweenRootAndChildren_ReturnsCompleteOldThenNewSnapshot()
    {
        var trip = await SeedAsync();
        var gate = new ReadGate();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, gate);
        using var reader = app.GetTestClient();
        using var writer = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(writer, app.Services, trip.UserId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(writer);
        var pending = reader.GetAsync($"/api/trips/{trip.Id}");
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var moved = await writer.PutAsJsonAsync($"/api/trips/{trip.Id}/editor/places/{Place(trip).Id}",
                PlaceDraft(trip, "After", true));
            Assert.True(moved.StatusCode == HttpStatusCode.OK, gate.Failure?.ToString() ?? await moved.Content.ReadAsStringAsync());
            gate.Release.TrySetResult();
            using var before = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            using var oldJson = await ReadJsonAsync(before);
            Assert.Equal(trip.UpdatedAt, oldJson.RootElement.GetProperty("updatedAt").GetDateTime());
            Assert.Equal(Place(trip).Id, oldJson.RootElement.GetProperty("regions")[0].GetProperty("places")[0].GetProperty("id").GetGuid());
            Assert.Equal(23d, oldJson.RootElement.GetProperty("regions")[0].GetProperty("places")[0].GetProperty("location")[0].GetDouble());
            Assert.True(oldJson.RootElement.GetProperty("segments")[0].GetProperty("hasCustomRoute").GetBoolean());
            using var oldRoute = JsonDocument.Parse(oldJson.RootElement.GetProperty("segments")[0].GetProperty("routeJson").GetString()!);
            Assert.Equal(23d, oldRoute.RootElement.GetProperty("coordinates")[0][0].GetDouble());
            using var after = await reader.GetAsync($"/api/trips/{trip.Id}");
            using var newJson = await ReadJsonAsync(after);
            Assert.NotEqual(trip.UpdatedAt, newJson.RootElement.GetProperty("updatedAt").GetDateTime());
            Assert.Empty(newJson.RootElement.GetProperty("regions")[0].GetProperty("places").EnumerateArray());
            var changed = newJson.RootElement.GetProperty("regions")[1].GetProperty("places")[0];
            Assert.Equal(Place(trip).Id, changed.GetProperty("id").GetGuid());
            Assert.Equal(25d, changed.GetProperty("location")[0].GetDouble());
            Assert.True(newJson.RootElement.GetProperty("segments")[0].GetProperty("hasCustomRoute").GetBoolean());
            using var newRoute = JsonDocument.Parse(newJson.RootElement.GetProperty("segments")[0].GetProperty("routeJson").GetString()!);
            Assert.Equal(25d, newRoute.RootElement.GetProperty("coordinates")[0][0].GetDouble());
            AssertNoWrites(gate);
        }
        finally
        {
            gate.Release.TrySetResult();
            using var drained = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    /// <summary>Old public authority can reveal only old public content, including for a wrong-owner bearer.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicToPrivateBetweenQueries_NeverDisclosesNewPrivateContent(bool wrongOwner)
    {
        var trip = await SeedAsync();
        var other = await fixture.CreateUserAsync();
        await TokenAsync(other.Id);
        var gate = new ReadGate();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, gate);
        using var reader = app.GetTestClient();
        if (wrongOwner) reader.DefaultRequestHeaders.Authorization = new("Bearer", other.Id);
        using var writer = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(writer, app.Services, trip.UserId);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(writer);
        var pending = reader.GetAsync($"/api/trips/{trip.Id}");
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var hidden = await writer.PatchAsJsonAsync($"/api/trips/{trip.Id}/editor/metadata", new
            {
                name = trip.Name, notesHtml = (string?)null, isPublic = false,
                coverImage = (object?)null, center = (object?)null, zoom = (int?)null
            });
            Assert.True(hidden.StatusCode == HttpStatusCode.OK, gate.Failure?.ToString() ?? await hidden.Content.ReadAsStringAsync());
            using var edited = await writer.PutAsJsonAsync($"/api/trips/{trip.Id}/editor/places/{Place(trip).Id}",
                PlaceDraft(trip, "PRIVATE-SENTINEL-852", false));
            Assert.True(edited.StatusCode == HttpStatusCode.OK, gate.Failure?.ToString() ?? await edited.Content.ReadAsStringAsync());
            gate.Release.TrySetResult();
            using var before = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            using var json = await ReadJsonAsync(before);
            Assert.True(json.RootElement.GetProperty("isPublic").GetBoolean());
            Assert.DoesNotContain("PRIVATE-SENTINEL-852", json.RootElement.GetRawText());
            using var fresh = await reader.GetAsync($"/api/trips/{trip.Id}");
            Assert.Equal(HttpStatusCode.Unauthorized, fresh.StatusCode);
            Assert.DoesNotContain("PRIVATE-SENTINEL-852", await fresh.Content.ReadAsStringAsync());
            AssertNoWrites(gate);
        }
        finally
        {
            gate.Release.TrySetResult();
            using var drained = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    /// <summary>The token row and active-account lookup share the earlier private-owner snapshot.</summary>
    [PostgresFact]
    public async Task UserDeactivationBetweenTokenAndUser_IsVisibleOnlyOnNextRead()
    {
        var trip = await SeedAsync(false);
        var gate = new ReadGate(identity: true);
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, gate);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", trip.UserId);
        var pending = client.GetAsync($"/api/trips/{trip.Id}");
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using (var writer = fixture.CreateContext())
                await writer.Users.Where(user => user.Id == trip.UserId).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsActive, false));
            gate.Release.TrySetResult();
            using var before = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            using var json = await ReadJsonAsync(before);
            Assert.Equal(trip.Id, json.RootElement.GetProperty("id").GetGuid());
            using var fresh = await client.GetAsync($"/api/trips/{trip.Id}");
            Assert.Equal(HttpStatusCode.Unauthorized, fresh.StatusCode);
            AssertNoWrites(gate);
        }
        finally
        {
            gate.Release.TrySetResult();
            using var drained = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    /// <summary>Caller cancellation and a terminated read backend both end the snapshot and permit a clean next read.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRead_CleansTransactionAndContextWithoutWrites(bool databaseFailure)
    {
        var trip = await SeedAsync();
        var gate = new ReadGate { TripId = trip.Id };
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, gate);
        using var client = app.GetTestClient();
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync($"/api/trips/{trip.Id}", cancellation.Token);
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(System.Data.IsolationLevel.RepeatableRead, gate.Isolation);
            Assert.Equal("on", gate.ReadOnly);
            Assert.Equal("30s", gate.Timeout);
            if (databaseFailure)
            {
                await using var connection = fixture.CreateConnection();
                await connection.OpenAsync();
                await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
                terminate.Parameters.AddWithValue("pid", gate.BackendPid);
                Assert.Equal(true, await terminate.ExecuteScalarAsync());
                gate.Release.TrySetResult();
                using var failure = await pending.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
                Assert.DoesNotContain(Place(trip).Id.ToString(), await failure.Content.ReadAsStringAsync());
            }
            else
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    using var response = await pending.WaitAsync(TimeSpan.FromSeconds(15));
                });
            }
            await gate.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await AssertNoTransactionAsync(gate.BackendPid);
            Assert.All(gate.Contexts, context => Assert.Throws<ObjectDisposedException>(() => context.Model));
            using var fresh = await client.GetAsync($"/api/trips/{trip.Id}");
            using var json = await ReadJsonAsync(fresh);
            Assert.Equal(trip.UpdatedAt, json.RootElement.GetProperty("updatedAt").GetDateTime());
            AssertNoWrites(gate);
            await using var clean = fixture.CreateConnection();
            await clean.OpenAsync();
            await using var settings = new NpgsqlCommand("SELECT current_setting('transaction_read_only'), current_setting('transaction_timeout')", clean);
            await using var values = await settings.ExecuteReaderAsync();
            Assert.True(await values.ReadAsync());
            Assert.Equal("off", values.GetString(0));
            Assert.Equal("0", values.GetString(1));
        }
        finally
        {
            gate.Release.TrySetResult();
            try { using var drained = await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    /// <summary>Observes only the recorded fixture-owned backend, accepting a closed broken session.</summary>
    private async Task AssertNoTransactionAsync(int pid)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid = @pid AND xact_start IS NOT NULL", connection);
        command.Parameters.AddWithValue("pid", pid);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    /// <summary>Uses production registrations, independent contexts and the actual API error middleware.</summary>
    private Task<WebApplication> StartAsync(ApplicationDbContext db, ReadGate gate,
        Action<IServiceCollection>? configure = null)
    {
        var root = CreateTestDirectory();
        var markers = Path.Combine(root, "icons", "wayfarer-map-icons", "dist", "marker");
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, "marker.svg"), "<svg/>");
        return IdentityRouteHost.StartAsync(db, root, services =>
        {
            services.AddHttpContextAccessor();
            services.AddScoped(provider => fixture.CreateContext(
                provider.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Method == "GET" ? [gate] : []));
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new ReadFactory(fixture, gate));
            services.AddSingleton(Mock.Of<ICacheWarmupScheduler>());
            services.AddSingleton(Mock.Of<ITripMapThumbnailGenerator>());
            services.AddSingleton(Mock.Of<ITripEditorGeocodeSearchService>());
            var icons = new Mock<IIconColorProvider>();
            icons.Setup(item => item.GetAvailableColors()).Returns(new IconColorClasses(["bg-blue"], []));
            services.AddSingleton(icons.Object);
            configure?.Invoke(services);
        }, app =>
        {
            app.Environment.WebRootPath = root;
            app.UseMiddleware<ApiErrorResponseMiddleware>();
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (Exception exception) { gate.Failure = exception; throw; }
                finally
                {
                    if (context.Request.Path == $"/api/trips/{gate.TripId}") gate.Completed.TrySetResult();
                }
            });
        });
    }

    /// <summary>Seeds a small route whose Web coordinate move invalidates custom geometry atomically.</summary>
    private async Task<Trip> SeedAsync(bool isPublic = true)
    {
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Snapshot", IsPublic = isPublic };
        fixture.RegisterTrip(trip.Id);
        var from = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "From", DisplayOrder = 1 };
        var to = new Region { Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, Name = "To", DisplayOrder = 9 };
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = from, UserId = user.Id, Name = "Before", DisplayOrder = 7,
            Location = new Point(23, 37) { SRID = 4326 }, IconName = "marker", MarkerColor = "bg-blue"
        };
        var segment = new Segment
        {
            Id = Guid.NewGuid(), Trip = trip, UserId = user.Id, FromPlace = place, ToPlace = place,
            Mode = "walk", DisplayOrder = 3,
            RouteGeometry = new LineString([new(23, 37), new(24, 38), new(23, 37)]) { SRID = 4326 }
        };
        await using var db = fixture.CreateContext();
        db.Regions.AddRange(from, to);
        db.Places.Add(place);
        db.Segments.Add(segment);
        await db.SaveChangesAsync();
        await db.Entry(trip).ReloadAsync();
        await TokenAsync(user.Id);
        return trip;
    }

    /// <summary>Creates genuine fixture-owned hashed incoming bearer authority.</summary>
    private async Task TokenAsync(string userId)
    {
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { UserId = userId, User = null!, Name = "852 fixture", TokenHash = ApiTokenService.HashToken(userId) });
        await db.SaveChangesAsync();
    }

    /// <summary>Returns the seeded mutable anchor.</summary>
    private static Place Place(Trip trip) => trip.Regions.OrderBy(region => region.DisplayOrder).First().Places.Single();

    /// <summary>Supplies the complete real Web editor draft, without external reverse geocoding.</summary>
    private static object PlaceDraft(Trip trip, string name, bool move) => new
    {
        regionId = trip.Regions.OrderBy(region => region.DisplayOrder).ElementAt(move ? 1 : 0).Id,
        name, notesHtml = (string?)null, address = (string?)null,
        location = new { latitude = move ? 39d : 37d, longitude = move ? 25d : 23d },
        iconName = "marker", markerColor = "bg-blue", reverseGeocode = false
    };

    /// <summary>Parses actual HTTP wire JSON only after checking the successful response.</summary>
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Asserts read contexts never saved changes or issued domain DML.</summary>
    private static void AssertNoWrites(ReadGate gate)
    {
        Assert.NotEmpty(gate.Contexts);
        Assert.Equal(0, gate.Saves);
        Assert.DoesNotContain(gate.Commands, sql => sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Uses the existing factory seam to give each read a fresh instrumented PostgreSQL context.</summary>
    private sealed class ReadFactory(PostgresImportTestFixture fixture, ReadGate gate) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            var db = fixture.CreateContext(gate);
            db.SavingChanges += (_, _) => gate.Saves++;
            gate.Contexts.Add(db);
            return db;
        }
    }

    /// <summary>Schedules real SQL without altering commands or results; both paths support the red-first action.</summary>
    private sealed class ReadGate(bool identity = false, bool pause = true) : DbCommandInterceptor
    {
        internal int Saves;
        internal Guid TripId;
        internal Exception? Failure;
        internal System.Data.IsolationLevel? Isolation;
        internal string? ReadOnly;
        internal string? Timeout;
        internal List<ApplicationDbContext> Contexts { get; } = [];
        internal int BackendPid { get; private set; }
        internal List<string> Commands { get; } = [];
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result)
        {
            PauseAsync(command, CancellationToken.None).GetAwaiter().GetResult();
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await PauseAsync(command, cancellationToken);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        /// <summary>Blocks once before the first child or user query, after its preceding result is consumed.</summary>
        private async Task PauseAsync(DbCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command.CommandText);
            if (!pause || Reached.Task.IsCompleted || !(identity ? command.CommandText.Contains("FROM \"AspNetUsers\"")
                : command.CommandText.Contains("JOIN \"Regions\""))) return;
            BackendPid = ((NpgsqlConnection)command.Connection!).ProcessID;
            Isolation = command.Transaction?.IsolationLevel;
            await using var settings = new NpgsqlCommand(
                "SELECT current_setting('transaction_read_only'), current_setting('transaction_timeout')",
                (NpgsqlConnection)command.Connection, (NpgsqlTransaction?)command.Transaction);
            await using (var values = await settings.ExecuteReaderAsync(cancellationToken))
            {
                await values.ReadAsync(cancellationToken);
                ReadOnly = values.GetString(0);
                Timeout = values.GetString(1);
            }
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }
}
