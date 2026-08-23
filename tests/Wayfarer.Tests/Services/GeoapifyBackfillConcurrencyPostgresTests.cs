using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Proves one durable per-user backfill owner spans selection, admission, contact, and persistence.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class GeoapifyBackfillConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task ConcurrentSameUserInvocationsContactOnceWhileAnotherUserRemainsIndependent()
    {
        var user = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, other.Id, protection);
        var handler = new CoordinatedHandler(user.Id, other.Id);
        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        await using var otherDb = fixture.CreateContext();
        var first = Service(firstDb, protection, handler);
        var second = Service(secondDb, protection, handler);
        var independent = Service(otherDb, protection, handler);

        var firstRun = first.RunAsync(user.Id);
        await handler.FirstUserRequestEntered;
        var secondRun = second.RunAsync(user.Id);
        var otherRun = independent.RunAsync(other.Id);
        await handler.OtherUserRequestEntered;
        var ownershipObserved = ObserveLockOrDuplicateContactAsync(handler, user.Id);
        await ownershipObserved;
        handler.Release();
        await Task.WhenAll(firstRun, secondRun, otherRun);

        await using var verify = fixture.CreateContext();
        Assert.Equal(1, handler.RequestsFor(user.Id));
        Assert.Equal(1, await verify.Locations.CountAsync(item => item.UserId == user.Id && item.ReverseGeocodingProvider == "geoapify"));
        Assert.Equal(1, handler.RequestsFor(other.Id));
    }

    private async Task ObserveLockOrDuplicateContactAsync(CoordinatedHandler handler, string userId)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        while (handler.RequestsFor(userId) < 2)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE wait_event_type = 'Lock'
                AND (query LIKE '%pg_advisory_xact_lock%' OR query LIKE '%AspNetUsers%FOR UPDATE%'))
                """, connection);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Yield();
        }
    }

    private async Task SeedAsync(string userId, string otherUserId, IDataProtectionProvider protection)
    {
        await using var db = fixture.CreateContext();
        foreach (var id in new[] { userId, otherUserId })
        {
            var profile = PersonalLocationProviderProfile.Create(id, PersonalLocationProvider.Geoapify);
            new PersonalProviderCredentialService(protection).Replace(profile, $"key-{id}");
            profile.GeocodingAuthorized = true;
            profile.GeocodingVerification = PersonalProviderVerification.Verified;
            profile.GeocodingVerifiedCredentialGeneration = profile.CredentialGeneration;
            profile.GeocodingVerifiedConfigurationGeneration = profile.GeocodingGeneration;
            db.Add(profile);
            db.Add(new PersonalLocationProviderSelection { UserId = id, GeocodingProviderKey = "geoapify" });
            db.Locations.Add(new Location
            {
                UserId = id, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC",
                Coordinates = new Point(20, 10) { SRID = 4326 }
            });
        }
        await db.SaveChangesAsync();
    }

    private static GeoapifyLocationBackfillService Service(ApplicationDbContext db,
        IDataProtectionProvider protection, CoordinatedHandler handler)
    {
        var credentials = new PersonalProviderCredentialService(protection);
        var gate = new PersonalProviderContactGate(db, credentials,
            new LegacyMapboxMigrationService(db, credentials), new ConfigurationBuilder().Build());
        var reverse = new ReverseGeocodingService(new HttpClient(handler), NullLogger<BaseApiController>.Instance, gate, db);
        return new GeoapifyLocationBackfillService(db, reverse);
    }

    private sealed class CoordinatedHandler(string primaryUserId, string otherUserId) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _other = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, int> _requests = [];
        public Task FirstUserRequestEntered => _first.Task;
        public Task OtherUserRequestEntered => _other.Task;
        public int RequestsFor(string userId) { lock (_requests) return _requests.GetValueOrDefault(userId); }
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.Query.Split("apiKey=", StringSplitOptions.None)[1];
            var userId = Uri.UnescapeDataString(key)[4..];
            lock (_requests) _requests[userId] = _requests.GetValueOrDefault(userId) + 1;
            if (userId == primaryUserId) _first.TrySetResult();
            if (userId == otherUserId) _other.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"type":"FeatureCollection","features":[{"properties":{"formatted":"Address","address_line1":"Address"}}]}
                    """)
            };
        }
    }
}
