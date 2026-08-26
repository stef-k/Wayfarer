using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Npgsql;
using System.Data.Common;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Provides shared PostgreSQL backfill fixtures, gates, and provider handlers.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    private async Task ObserveLockOrDuplicateContactAsync(CoordinatedHandler handler, string userId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = fixture.CreateConnection();
        try
        {
            await connection.OpenAsync(timeout.Token);
            while (handler.RequestsFor(userId) < 2)
            {
                await using var command = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE wait_event_type = 'Lock'
                    AND (query LIKE '%pg_advisory_xact_lock%' OR query LIKE '%AspNetUsers%FOR UPDATE%'))
                    """, connection);
                if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
                await Task.Yield();
                timeout.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail("The competing same-user backfill did not enter the expected PostgreSQL lock wait within 10 seconds.");
        }
    }

    private async Task SeedAsync(string userId, string? otherUserId, IDataProtectionProvider protection)
    {
        await using var db = fixture.CreateContext();
        foreach (var id in new[] { userId, otherUserId }.OfType<string>())
        {
            var profile = PersonalLocationProviderProfile.Create(id, PersonalLocationProvider.Geoapify);
            new PersonalProviderCredentialService(protection).Replace(profile, $"key-{id}");
            profile.GeocodingAuthorized = true;
            profile.GeocodingVerification = PersonalProviderVerification.Verified;
            profile.GeocodingVerifiedCredentialGeneration = profile.CredentialGeneration;
            profile.GeocodingVerifiedConfigurationGeneration = profile.GeocodingGeneration;
            db.Add(profile);
            db.Add(new PersonalLocationProviderSelection { UserId = id, GeocodingProviderKey = "geoapify" });
            db.Add(new GeoapifyUsageGuard { UserId = id });
            db.Locations.Add(new Location
            {
                UserId = id, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC",
                Coordinates = new Point(20, 10) { SRID = 4326 }
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedMapboxAsync(string userId, IDataProtectionProvider protection)
    {
        await using var db = fixture.CreateContext();
        var profile = PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Mapbox);
        new PersonalProviderCredentialService(protection).Replace(profile, $"key-{userId}");
        profile.GeocodingAuthorized = true;
        profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        new PersonalProviderCredentialService(protection).RecordVerification(profile,
            PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        db.Add(profile);
        db.Add(new PersonalLocationProviderSelection { UserId = userId, GeocodingProviderKey = "mapbox" });
        db.Locations.Add(new Location
        {
            UserId = userId, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC", Coordinates = new Point(20, 10) { SRID = 4326 }
        });
        await db.SaveChangesAsync();
    }

    private GeoapifyLocationBackfillService Service(IDataProtectionProvider protection,
        HttpMessageHandler handler,
        Func<PersonalProviderAuthoritySnapshot, CancellationToken, Task>? beforeFinalAuthorityValidation = null,
        params IInterceptor[] interceptors)
    {
        var credentials = new PersonalProviderCredentialService(protection);
        var contextFactory = new FixtureDbContextFactory(fixture, interceptors);
        var services = new ServiceCollection()
            .AddScoped(_ => fixture.CreateContext())
            .AddSingleton(credentials)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddScoped<LegacyMapboxMigrationService>()
            .AddScoped<PersonalProviderContactGate>()
            .BuildServiceProvider();
        var authority = new LocationEnrichmentExecutionAuthority(contextFactory);
        return new GeoapifyLocationBackfillService(contextFactory, services.GetRequiredService<IServiceScopeFactory>(),
            new TestHttpClientFactory(handler), NullLogger<BaseApiController>.Instance, authority)
        {
            BeforeFinalAuthorityValidationAsync = beforeFinalAuthorityValidation
                ?? (static (_, _) => Task.CompletedTask)
        };
    }

    private GeoapifyLocationBackfillService Service(ApplicationDbContext db,
        IDataProtectionProvider protection, CoordinatedHandler handler) => Service(protection, handler);

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        { Timeout = TimeSpan.FromSeconds(15) };
    }

    private sealed class FixtureDbContextFactory(PostgresImportTestFixture fixture, IInterceptor[] interceptors)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext(interceptors);
    }

    private sealed class LocationUpdateGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int gated;
        public Task Entered => entered.Task;
        public void Release() => release.TrySetResult();
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await GateAsync(command, cancellationToken);
            return result;
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await GateAsync(command, cancellationToken);
            return result;
        }
        private async Task GateAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!command.CommandText.Contains("UPDATE \"Locations\"", StringComparison.Ordinal)
                || Interlocked.Exchange(ref gated, 1) != 0) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Pauses after the advisory Location read so an independent context can commit a mutation.</summary>
    private sealed class CandidateReadGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int gated;
        public Task Entered => entered.Task;
        public void Release() => release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Locations\"", StringComparison.Ordinal)
                && command.CommandText.Contains("LIMIT 2", StringComparison.Ordinal)
                && !command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)
                && Interlocked.Exchange(ref gated, 1) == 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class CoordinatedHandler(
        string primaryUserId, string? otherUserId, ContactOutcome outcome = ContactOutcome.Success) : HttpMessageHandler
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
            var query = request.RequestUri!.Query;
            var key = query.Contains("apiKey=", StringComparison.Ordinal)
                ? query.Split("apiKey=", StringSplitOptions.None)[1]
                : query.Split("access_token=", StringSplitOptions.None)[1];
            var userId = Uri.UnescapeDataString(key)[4..];
            lock (_requests) _requests[userId] = _requests.GetValueOrDefault(userId) + 1;
            if (userId == primaryUserId) _first.TrySetResult();
            if (otherUserId != null && userId == otherUserId) _other.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            if (outcome == ContactOutcome.Timeout) throw new TaskCanceledException();
            if (outcome == ContactOutcome.ProviderFailure)
                return new(System.Net.HttpStatusCode.ServiceUnavailable);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"type":"FeatureCollection","features":[{"properties":{"formatted":"Address","address_line1":"Address"}}]}
                    """)
            };
        }
    }

    public enum AuthorityMutation { ReplaceCredential, RevokeCredential, ChangeSelection, ChangeCapabilityGeneration,
        ChangeVerificationState, ChangeVerifiedCredentialBinding, ChangeVerifiedCapabilityBinding, ChangeProfileIdentity }
    public enum OperationMutation { LeaseId, WorkflowEpoch, AttemptNumber, Capability, VerificationCredential, VerificationCapability }
    public enum PreContactMutation { VerificationBinding, CredentialBinding, MapboxConsentBinding }
    private enum ContactOutcome { Success, Timeout, ProviderFailure }
}
