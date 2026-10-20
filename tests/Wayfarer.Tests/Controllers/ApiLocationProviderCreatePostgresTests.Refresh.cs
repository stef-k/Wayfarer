using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Owner coordinate-refresh races through routed MVC, real PostgreSQL admission and controlled HTTP.</summary>
public sealed partial class ApiLocationProviderCreatePostgresTests
{
    /// <summary>Deletion after the editor read but before preparation forbids a new admission, including unchanged coordinates.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinateRefresh_DeletionBeforeAdmission_ReturnsMissingWithoutContact(bool changedCoordinates)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var key = Guid.NewGuid();
        var id = await SeedLocationAsync(owner.UserId, key);
        using var handler = new ControlledProvider();
        var events = new Mock<SseService>();
        events.Setup(service => service.BroadcastAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        Func<Task> delete = null!;
        var boundary = new BeforeRefreshPreparation(() => delete());
        await WithHostAsync(protection, handler, [boundary], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            delete = async () =>
            {
                Assert.Equal(0, await AdmissionCountAsync(owner.UserId, "geoapify"));
                Assert.Equal(0, handler.Calls);
                using var deletion = await client.DeleteAsync($"/api/location/{id}");
                Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
                await AssertRefreshDeletedAsync(client, owner.UserId, key);
                Assert.Equal(0, await AdmissionCountAsync(owner.UserId, "geoapify"));
                Assert.Equal(0, handler.Calls);
            };
            using var response = await client.PutAsJsonAsync($"/api/location/{id}",
                new { latitude = changedCoordinates ? 11 : 10, longitude = changedCoordinates ? 21 : 20 });
            Assert.True(boundary.Triggered);
            await AssertRefreshDeletedAsync(client, owner.UserId, key);
            var admissions = await AdmissionCountAsync(owner.UserId, "geoapify");
            output.WriteLine("Refresh after committed deletion: changed={0}, status={1}, admissions={2}, contacts={3}",
                changedCoordinates, (int)response.StatusCode, admissions, handler.Calls);
            Assert.Equal(0, admissions);
            Assert.Equal(0, handler.Calls);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Location not found.", body.RootElement.GetProperty("message").GetString());
        }, events.Object, visits.Object);
        Assert.All(events.Invocations, call => Assert.Contains("location-deleted", (string)call.Arguments[1]));
        visits.VerifyNoOtherCalls();
    }

    /// <summary>An admitted contact cannot be recalled, but its completion must respect a subsequent routed deletion.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinateRefresh_DeletionDuringHttp_DiscardsAlreadyAdmittedResult(bool changedCoordinates)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var key = Guid.NewGuid();
        var id = await SeedLocationAsync(owner.UserId, key);
        using var handler = new ControlledProvider();
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        var events = new Mock<SseService>();
        events.Setup(service => service.BroadcastAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        await WithHostAsync(protection, handler, [], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            handler.BeforeResponse = async _ =>
            {
                Assert.Equal(1, await AdmissionCountAsync(owner.UserId, "geoapify"));
                Assert.Equal(1, handler.Calls);
                using var deletion = await client.DeleteAsync($"/api/location/{id}");
                Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
                await AssertRefreshDeletedAsync(client, owner.UserId, key);
            };
            using var response = await client.PutAsJsonAsync($"/api/location/{id}",
                new { latitude = changedCoordinates ? 11 : 10, longitude = changedCoordinates ? 21 : 20, notes = "Stale patch" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertRefreshDeletedAsync(client, owner.UserId, key);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(1, await AdmissionCountAsync(owner.UserId, "geoapify"));
        }, events.Object, visits.Object);
        Assert.All(events.Invocations, call => Assert.Contains("location-deleted", (string)call.Arguments[1]));
        visits.VerifyNoOtherCalls();
    }

    /// <summary>Valid refreshes preserve provider products, optional-failure fallback, pending edits and freshness semantics.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("geoapify", false, "success")]
    [InlineData("mapbox", false, "success")]
    [InlineData("geoapify", true, "success")]
    [InlineData("geoapify", true, "failure")]
    [InlineData("geoapify", true, "none")]
    [InlineData("geoapify", false, "keyless")]
    public async Task CoordinateRefresh_CurrentRow_PreservesEditsAndProviderPolicy(string provider, bool changedCoordinates, string mode)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync(provider, protection);
        var key = mode == "keyless" ? (Guid?)null : Guid.NewGuid();
        var id = await SeedLocationAsync(owner.UserId, key);
        var observed = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        // Own the catalog prerequisite so these cases also run against a fresh, unseeded CI database.
        var activity = new ActivityType { Name = $"Refresh fixture {Guid.NewGuid():N}" };
        await using var catalog = fixture.CreateContext();
        catalog.ActivityTypes.Add(activity);
        try
        {
            await catalog.SaveChangesAsync();
            await using (var db = fixture.CreateContext())
            {
                var row = await db.Locations.SingleAsync(location => location.Id == id);
                row.CoordinateOrigin = "sensor";
                row.ObservedAtUtc = observed;
                row.ObservedAgeAtEventMs = 0;
                row.PresenceObservedAtUtc = observed;
                row.LocalTimestamp = observed;
                row.FullAddress = "Prior address";
                row.ReverseGeocodingProvider = "manual";
                row.ProviderAddressLine1 = "Prior display line";
                if (mode == "none")
                    (await db.PersonalLocationProviderSelections.SingleAsync(row => row.UserId == owner.UserId))
                        .Select(Wayfarer.Models.LocationProviders.PersonalProviderCapability.Geocoding, null);
                await db.SaveChangesAsync();
            }
            using var handler = new ControlledProvider {
                BeforeResponse = mode == "failure" ? _ => throw new HttpRequestException("Synthetic optional provider failure") : null };
            var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
            var events = new Mock<SseService>(MockBehavior.Strict);
            await WithHostAsync(protection, handler, [], async client =>
            {
                client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
                using var response = await client.PutAsJsonAsync($"/api/location/{id}", new {
                    latitude = changedCoordinates ? 11 : 10, longitude = changedCoordinates ? 21 : 20,
                    notes = "Owner patch", activityTypeId = activity.Id,
                    localTimestamp = changedCoordinates ? observed.AddHours(1) : (DateTime?)null });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                await using var db = fixture.CreateContext();
                var row = await db.Locations.AsNoTracking().SingleAsync(location => location.Id == id);
                Assert.Equal((changedCoordinates ? 21d : 20d, changedCoordinates ? 11d : 10d), (row.Coordinates.X, row.Coordinates.Y));
                Assert.Equal("Owner patch", row.Notes);
                Assert.Equal(activity.Id, row.ActivityTypeId);
                Assert.Equal(changedCoordinates ? observed.AddHours(1) : observed, row.LocalTimestamp);
                Assert.Equal(key, row.IdempotencyKey);
                Assert.Equal(changedCoordinates ? null : (DateTime?)observed, row.PresenceObservedAtUtc);
                Assert.Equal(changedCoordinates ? null : "sensor", row.CoordinateOrigin);
                var enriched = mode is "success" or "keyless";
                Assert.Equal(enriched ? "Controlled address" : "Prior address", row.FullAddress);
                Assert.Equal(enriched ? provider : null, row.ReverseGeocodingProvider);
                Assert.Equal(enriched ? provider == "geoapify" ? "persistent" : "permanent" : null, row.ReverseGeocodingStorageMode);
                Assert.Equal(enriched, row.ReverseGeocodedAt.HasValue);
                Assert.Equal(enriched && provider == "geoapify" ? "12 Main Street" : null, row.ProviderAddressLine1);
                Assert.Equal(mode == "none" ? 0 : 1, handler.Calls);
                Assert.Equal(handler.Calls, await AdmissionCountAsync(owner.UserId, provider));
            }, events.Object, visits.Object);
            visits.VerifyNoOtherCalls();
            events.VerifyNoOtherCalls();
        }
        finally
        {
            // Remove only this case's FK-bearing row and Activity; the shared fixture still owns its user cleanup.
            await using var cleanup = fixture.CreateContext();
            await cleanup.Locations.Where(row => row.UserId == owner.UserId && row.Id == id).ExecuteDeleteAsync();
            await cleanup.ActivityTypes.Where(row => row.Id == activity.Id && row.Name == activity.Name).ExecuteDeleteAsync();
        }
    }

    /// <summary>A newer manual tuple and pending editor fields win before admission, during HTTP or before completion.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("before-admission")]
    [InlineData("during-http")]
    [InlineData("before-save")]
    public async Task CoordinateRefresh_NewerOwnerEdit_RejectsStalePatch(string boundary)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var id = await SeedLocationAsync(owner.UserId, Guid.NewGuid());
        async Task MutateAsync()
        {
            await using var db = fixture.CreateContext();
            var row = await db.Locations.SingleAsync(location => location.Id == id);
            row.Coordinates = new(22, 12) { SRID = 4326 };
            row.FullAddress = "Newer manual address";
            row.ReverseGeocodingProvider = "manual";
            row.Notes = "Newer owner notes";
            row.LocalTimestamp = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }
        using var handler = new ControlledProvider { BeforeResponse = boundary == "during-http" ? _ => MutateAsync() : null };
        IInterceptor[] interceptors = boundary switch {
            "before-admission" => [new BeforeRefreshPreparation(MutateAsync)],
            "before-save" => [new BeforeFence(2, MutateAsync)],
            _ => [] };
        await WithHostAsync(protection, handler, interceptors, async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            using var response = await client.PutAsJsonAsync($"/api/location/{id}",
                new { latitude = 10, longitude = 20, notes = "Stale owner patch", clearActivity = true });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await using var db = fixture.CreateContext();
            var current = await db.Locations.AsNoTracking().SingleAsync(location => location.Id == id);
            Assert.Equal((22d, 12d), (current.Coordinates.X, current.Coordinates.Y));
            Assert.Equal("Newer owner notes", current.Notes);
            Assert.Equal("Newer manual address", current.FullAddress);
            Assert.Equal("manual", current.ReverseGeocodingProvider);
            Assert.Equal(new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc), current.LocalTimestamp);
            Assert.Equal(boundary == "before-admission" ? 0 : 1, handler.Calls);
            Assert.Equal(handler.Calls, await AdmissionCountAsync(owner.UserId, "geoapify"));
        });
    }

    /// <summary>Provider authority changed after HTTP cannot authorize persistence of the old enrichment tuple.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("geoapify", "selection")]
    [InlineData("geoapify", "credential")]
    [InlineData("mapbox", "consent")]
    public async Task CoordinateRefresh_ChangedProviderAuthority_DiscardsResult(string provider, string mutation)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync(provider, protection);
        var id = await SeedLocationAsync(owner.UserId, Guid.NewGuid());
        var boundary = new BeforeAddressPersistence(async () =>
        {
            await using var db = fixture.CreateContext();
            if (mutation == "selection")
                (await db.PersonalLocationProviderSelections.SingleAsync(row => row.UserId == owner.UserId))
                    .Select(Wayfarer.Models.LocationProviders.PersonalProviderCapability.Geocoding, null);
            else
            {
                var profile = await db.PersonalLocationProviderProfiles.SingleAsync(row => row.UserId == owner.UserId);
                if (mutation == "consent") profile.ClearPermanentGeocodingConsent();
                else CredentialTestFactory.Create(protection).Replace(profile, "replacement-synthetic");
            }
            await db.SaveChangesAsync();
        });
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [boundary], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            using var response = await client.PutAsJsonAsync($"/api/location/{id}",
                new { latitude = 11, longitude = 21, notes = "Valid coordinate patch" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(boundary.Applied);
            await using var db = fixture.CreateContext();
            var current = await db.Locations.AsNoTracking().SingleAsync(row => row.Id == id);
            Assert.Equal((21d, 11d), (current.Coordinates.X, current.Coordinates.Y));
            Assert.Equal("Valid coordinate patch", current.Notes);
            Assert.Null(current.FullAddress);
            Assert.Null(current.ReverseGeocodingProvider);
            Assert.Null(current.ReverseGeocodedAt);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(1, await AdmissionCountAsync(owner.UserId, provider));
        });
    }

    /// <summary>Checks the persisted deletion decision and the owner's public status response in fresh contexts.</summary>
    private async Task AssertRefreshDeletedAsync(HttpClient client, string userId, Guid key)
    {
        await using var db = fixture.CreateContext();
        Assert.False(await db.Locations.AnyAsync(row => row.UserId == userId));
        Assert.True(await db.Set<LocationDeletionMarker>().AnyAsync(row => row.UserId == userId && row.IdempotencyKey == key));
        using var status = await client.PostAsJsonAsync("/api/location/key-status", new { keys = new[] { key.ToString() } });
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("deleted", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
            .GetProperty("state").GetString());
    }

    /// <summary>Pauses at the review's first provider-selection lock, before any new admission or account fence.</summary>
    private sealed class BeforeRefreshPreparation(Func<Task> mutate) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }

        /// <summary>The editor must already have read its owned Location when the independent mutation commits.</summary>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains("PersonalLocationProviderSelections", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                Triggered = true;
                await mutate();
            }
            return result;
        }
    }
}
