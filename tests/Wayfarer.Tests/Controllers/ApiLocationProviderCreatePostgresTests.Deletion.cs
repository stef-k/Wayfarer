using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Routed deletion, privacy and provider/effect races using the existing authenticated provider host.</summary>
public sealed partial class ApiLocationProviderCreatePostgresTests
{
    /// <summary>All existing deletion authorities retain their authentication and suppress both ingest routes without effects.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("single")]
    [InlineData("bulk")]
    [InlineData("all")]
    public async Task OwnerDeletionRoutes_SuppressReplayAndKeepAuthority(string deletion)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var other = await SeedAsync("geoapify", protection);
        var key = Guid.NewGuid();
        var id = await SeedLocationAsync(owner.UserId, key);
        var foreign = await SeedLocationAsync(other.UserId, key);
        using var handler = new ControlledProvider();
        var events = new Mock<SseService>();
        events.Setup(service => service.BroadcastAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        await WithHostAsync(protection, handler, [], async client =>
        {
            using (var denied = new HttpRequestMessage(HttpMethod.Delete, $"/api/location/{id}"))
            {
                denied.Headers.Authorization = new("Bearer", other.Token);
                using var response = await client.SendAsync(denied);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            if (deletion == "single")
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/location/{id}");
                request.Headers.Authorization = new("Bearer", owner.Token);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            else
            {
                var auth = OwnerCookie(owner.UserId);
                client.DefaultRequestHeaders.Add("Cookie", auth);
                var url = deletion == "bulk" ? "/api/location/bulk-delete" : $"/api/users/{owner.UserId}/locations";
                using var missingAntiforgery = deletion == "bulk"
                    ? await client.PostAsJsonAsync(url, new { locationIds = new[] { id } }) : await client.DeleteAsync(url);
                Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
                using var tokens = await client.GetAsync("/test-antiforgery");
                tokens.EnsureSuccessStatusCode();
                client.DefaultRequestHeaders.Remove("Cookie");
                client.DefaultRequestHeaders.Add("Cookie", auth + "; " + tokens.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
                client.DefaultRequestHeaders.Add("RequestVerificationToken", await tokens.Content.ReadAsStringAsync());
                if (deletion == "all")
                {
                    using var denied = await client.DeleteAsync($"/api/users/{other.UserId}/locations");
                    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                }
                using var response = deletion == "bulk"
                    ? await client.PostAsJsonAsync(url, new { locationIds = new[] { id, foreign, int.MaxValue } })
                    : await client.DeleteAsync(url);
                Assert.Equal(deletion == "bulk" ? HttpStatusCode.OK : HttpStatusCode.NoContent, response.StatusCode);
                client.DefaultRequestHeaders.Remove("Cookie");
                client.DefaultRequestHeaders.Remove("RequestVerificationToken");
            }
            var before = events.Invocations.Count;
            foreach (var route in new[] { "check-in", "log-location" })
            {
                using var request = Request(route, owner.Token, key);
                using var response = await client.SendAsync(request);
                await AssertDeletedAsync(response);
            }
            Assert.Equal(before, events.Invocations.Count);
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.Locations.AnyAsync(row => row.Id == id));
            Assert.True(await verify.Locations.AnyAsync(row => row.Id == foreign));
            Assert.Single(await verify.Set<LocationDeletionMarker>().Where(row => row.UserId == owner.UserId).ToListAsync());
            Assert.False(await verify.Set<LocationDeletionMarker>().AnyAsync(row => row.UserId == other.UserId));
            Assert.Equal(0, handler.Calls);
            Assert.Equal(0, await AdmissionCountAsync(owner.UserId, "geoapify"));
            visits.VerifyNoOtherCalls();
        }, events.Object, visits.Object);
    }

    /// <summary>Deletion in each short-transaction gap wins without stale address, Visit or Location-success publication.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("check-in", 1)]
    [InlineData("log-location", 1)]
    [InlineData("check-in", 2)]
    [InlineData("log-location", 2)]
    [InlineData("check-in", 0)]
    [InlineData("log-location", 0)]
    [InlineData("check-in", 5)]
    [InlineData("log-location", 5)]
    public async Task DeletionAcrossCreationAdmissionHttpAndEffects_IsTerminal(string route, int fenceOrdinal)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var key = Guid.NewGuid();
        async Task DeleteWinner()
        {
            if (fenceOrdinal == 1) await SeedLocationAsync(owner.UserId, key);
            await using var deletion = fixture.CreateContext();
            var id = await deletion.Locations.Where(row => row.UserId == owner.UserId).Select(row => row.Id).SingleAsync();
            Assert.Single(await LocationDeletionFence.DeleteAsync(deletion, owner.UserId, [id], null, CancellationToken.None));
        }
        var boundary = new BeforeFence(fenceOrdinal, DeleteWinner);
        using var handler = new ControlledProvider { BeforeResponse = fenceOrdinal == 0 ? _ => DeleteWinner() : null };
        var events = new Mock<SseService>(MockBehavior.Strict);
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        await WithHostAsync(protection, handler, [boundary], async client =>
        {
            using var request = Request(route, owner.Token, key);
            var instant = DateTime.UtcNow.AddSeconds(-1);
            request.Content = JsonContent.Create(new { latitude = 10, longitude = 20, accuracy = 5,
                timestamp = instant, coordinateOrigin = "sensor", observedAtUtc = instant, observedAgeAtEventMs = 0 });
            using var response = await client.SendAsync(request);
            await AssertDeletedAsync(response);
            Assert.True(fenceOrdinal == 0 || boundary.Triggered);
            Assert.Equal(fenceOrdinal is 0 or 5 ? 1 : 0, handler.Calls);
            Assert.Equal(handler.Calls, await AdmissionCountAsync(owner.UserId, "geoapify"));
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.Locations.AnyAsync(row => row.UserId == owner.UserId));
            Assert.True(await verify.Set<LocationDeletionMarker>().AnyAsync(row => row.UserId == owner.UserId && row.IdempotencyKey == key));
            using var replayRequest = Request(route, owner.Token, key);
            using var replay = await client.SendAsync(replayRequest);
            await AssertDeletedAsync(replay);
        }, events.Object, visits.Object);
        events.VerifyNoOtherCalls();
        visits.VerifyNoOtherCalls();
    }

    /// <summary>Moves and edits remain present; foreign, keyless and never-seen history are never deletion evidence.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task KeyStatus_IsOwnerOnlyCoherentBoundedAndPrivate()
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var other = await SeedAsync("geoapify", protection);
        var present = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var id = await SeedLocationAsync(owner.UserId, present);
        var deletedId = await SeedLocationAsync(owner.UserId, deleted);
        await SeedLocationAsync(other.UserId, foreign);
        await SeedLocationAsync(owner.UserId, Guid.Empty);
        await SeedLocationAsync(owner.UserId, null);
        await using (var db = fixture.CreateContext())
            await LocationDeletionFence.DeleteAsync(db, owner.UserId, [deletedId], null, CancellationToken.None);
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            var moved = DateTime.UtcNow.AddDays(-5).Date;
            using var edit = await client.PutAsJsonAsync($"/api/location/{id}", new { localTimestamp = moved, notes = "Private owner notes" });
            edit.EnsureSuccessStatusCode();
            using var response = await client.PostAsJsonAsync("/api/location/key-status", new {
                keys = new[] { present.ToString("B"), deleted.ToString(), unknown.ToString(), foreign.ToString(), Guid.Empty.ToString(), present.ToString() } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var results = body.RootElement.GetProperty("results").EnumerateArray().ToList();
            Assert.Equal(5, results.Count);
            Assert.Equal(new[] { "present", "deleted", "unknown", "unknown", "present" },
                results.Select(row => row.GetProperty("state").GetString()));
            Assert.Equal(id, results[0].GetProperty("locationId").GetInt32());
            Assert.Equal(moved, results[0].GetProperty("localTimestamp").GetDateTime());
            Assert.EndsWith("Z", results[0].GetProperty("localTimestamp").GetString());
            Assert.All(results.Skip(1).Take(3), row => Assert.Equal(new[] { "key", "state" }, row.EnumerateObject().Select(property => property.Name)));
            Assert.DoesNotContain("Private", await response.Content.ReadAsStringAsync());
            foreach (var payload in new object[] { new { keys = Array.Empty<string>() }, new { keys = new[] { "bad" } },
                new { keys = new string?[] { null } }, new { keys = Enumerable.Repeat(present.ToString(), 101).ToArray() },
                new { keys = new object[] { 1 } },
                new { keys = new[] { present.ToString() }, userId = other.UserId } })
            {
                using var invalid = await client.PostAsJsonAsync("/api/location/key-status", payload);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.True(invalid.Headers.CacheControl?.NoStore);
            }
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Add("Cookie", OwnerCookie(owner.UserId));
            using var denied = await client.PostAsJsonAsync("/api/location/key-status", new { keys = new[] { deleted.ToString() } });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl?.NoStore);
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            await using (var db = fixture.CreateContext())
            {
                var inactive = await db.Users.SingleAsync(row => row.Id == owner.UserId);
                inactive.IsActive = false;
                await db.SaveChangesAsync();
            }
            using var inactiveResponse = await client.PostAsJsonAsync("/api/location/key-status", new { keys = new[] { deleted.ToString() } });
            Assert.Equal(HttpStatusCode.Unauthorized, inactiveResponse.StatusCode);
            Assert.Equal(0, handler.Calls);
            Assert.Equal(0, await AdmissionCountAsync(owner.UserId, "geoapify"));
        });
    }

    /// <summary>A deletion between statement execution and response materialization cannot mix marker and row snapshots.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task KeyStatus_UsesOneStatementSnapshotDuringDeletion()
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var key = Guid.NewGuid();
        var id = await SeedLocationAsync(owner.UserId, key);
        var snapshot = new AfterStatusSnapshot(async () =>
        {
            await using var deletion = fixture.CreateContext();
            await LocationDeletionFence.DeleteAsync(deletion, owner.UserId, [id], null, CancellationToken.None);
        });
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [snapshot], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            foreach (var state in new[] { "present", "deleted" })
            {
                using var response = await client.PostAsJsonAsync("/api/location/key-status", new { keys = new[] { key.ToString() } });
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(state, Assert.Single(body.RootElement.GetProperty("results").EnumerateArray()).GetProperty("state").GetString());
            }
            Assert.True(snapshot.Triggered);
            Assert.Equal(0, handler.Calls);
        });
    }

    /// <summary>An owner coordinate edit during enrichment cannot turn that edit into the automatic capture's Visit input.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task AutomaticVisit_AfterOwnerEdit_UsesOriginalCaptureCoordinates()
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        using var handler = new ControlledProvider
        {
            BeforeResponse = async _ =>
            {
                await using var db = fixture.CreateContext();
                var row = await db.Locations.SingleAsync(location => location.UserId == owner.UserId);
                row.Coordinates = new Point(21, 11) { SRID = 4326 };
                await db.SaveChangesAsync();
            }
        };
        var visits = new Mock<IPlaceVisitDetectionService>();
        visits.Setup(service => service.ProcessPingAsync(owner.UserId, It.IsAny<Point>(), It.IsAny<double?>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await WithHostAsync(protection, handler, [], async client =>
        {
            using var request = Request("log-location", owner.Token, Guid.NewGuid());
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            visits.Verify(service => service.ProcessPingAsync(owner.UserId,
                It.Is<Point>(point => point.X == 20 && point.Y == 10), 5, It.IsAny<CancellationToken>()), Times.Once);
            await using var verify = fixture.CreateContext();
            Assert.Equal((21d, 11d), await verify.Locations.Where(row => row.UserId == owner.UserId)
                .Select(row => new ValueTuple<double, double>(row.Coordinates.X, row.Coordinates.Y)).SingleAsync());
        }, visits: visits.Object);
    }

    /// <summary>Seeds payload-bearing owner history without invoking any provider, presence or Visit effects.</summary>
    private async Task<int> SeedLocationAsync(string userId, Guid? key)
    {
        await using var db = fixture.CreateContext();
        var row = new Location { UserId = userId, IdempotencyKey = key, TimeZoneId = "UTC",
            Timestamp = DateTime.UtcNow.AddDays(-10), LocalTimestamp = DateTime.UtcNow.AddDays(-10),
            Coordinates = new Point(20, 10) { SRID = 4326 }, Notes = "Private payload" };
        db.Locations.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Protects a real Identity cookie using the host's configured ticket format.</summary>
    private string OwnerCookie(string userId)
    {
        var services = _cookieServices ?? throw new InvalidOperationException("No routed host.");
        var scheme = IdentityConstants.ApplicationScheme;
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userId), new Claim(ClaimTypes.Role, "User")], "fixture"));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties {
            IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, scheme);
        return options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket);
    }

    // The existing per-test host supplies its cookie protector only for the duration of the callback.
    private IServiceProvider? _cookieServices;

    /// <summary>Confirms the exact terminal wire shape without a location link or retry hint.</summary>
    private static async Task AssertDeletedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "success", "code", "message" }, body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("location_deleted", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("Location was deleted.", body.RootElement.GetProperty("message").GetString());
    }

    /// <summary>Coordinates concurrent first-use reads before either request holds the account fence.</summary>
    private sealed class KeyReadBarrier : DbCommandInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM \"Locations\"", StringComparison.Ordinal)
                || !command.CommandText.Contains("\"IdempotencyKey\"", StringComparison.Ordinal)) return result;
            var count = Interlocked.Increment(ref _arrived);
            if (count > 2) return result;
            if (count == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    /// <summary>Runs deletion immediately before a selected real account-lock command, after earlier transactions ended.</summary>
    private sealed class BeforeFence(int ordinal, Func<Task> deletion) : DbCommandInterceptor
    {
        private int _count;
        public bool Triggered { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(", 838)", StringComparison.Ordinal)
                && Interlocked.Increment(ref _count) == ordinal)
            {
                Triggered = true;
                await deletion();
            }
            return result;
        }
    }

    /// <summary>Executes deletion after the real key-status UNION has taken its database snapshot.</summary>
    private sealed class AfterStatusSnapshot(Func<Task> deletion) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains("UNION ALL", StringComparison.Ordinal))
            {
                Triggered = true;
                await deletion();
            }
            return result;
        }
    }
}
