using System.Data.Common;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Proves retained personal-routing relational authority on guarded PostgreSQL.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class UserRoutingConfigurationPostgresTests(PostgresImportTestFixture fixture)
{
    private const string PreviousMigration = "20260819102433_RoutingProviderMinimumInterval";

    /// <summary>Proves migration backfill, future-user creation, constraints, FKs, and xmin recovery.</summary>
    [PostgresFact]
    public async Task RetainedConfiguration_MigrationConstraintsRelationshipsAndXmin_AreAuthoritative()
    {
        fixture.RequireAvailable();
        var legacyUserId = $"routing-legacy-{Guid.NewGuid():N}";
        var futureUserId = $"routing-future-{Guid.NewGuid():N}";
        var providerId = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync(PreviousMigration);
            await InsertUserAsync(context, legacyUserId);
            await migrator.MigrateAsync();
            Assert.Equal(1, await context.Set<UserRoutingConfiguration>().CountAsync(item => item.UserId == legacyUserId));
            Assert.Null((await context.Set<UserRoutingConfiguration>().AsNoTracking()
                .SingleAsync(item => item.UserId == legacyUserId)).SelectedProviderConfigurationId);

            await InsertUserAsync(context, futureUserId);
            Assert.Equal(1, await context.Set<UserRoutingConfiguration>().CountAsync(item => item.UserId == futureUserId));
            await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "UserRoutingConfigurations"
                SET "CredentialPresent" = TRUE, "CredentialCiphertext" = {{"ciphertext"}}
                WHERE "UserId" = {{legacyUserId}}
                """));

            await context.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "RoutingProviderConfigurations"
                    ("Id", "DisplayName", "AdapterType", "CredentialPresent", "CredentialRequired", "Enabled",
                     "ConfigurationVersion", "GenerationTimeoutSeconds", "ResponseSizeLimitBytes", "RequestsPerMinute",
                     "MinimumIntervalMilliseconds", "MaxConcurrency", "PersonalRoutingAccess")
                VALUES ({{providerId}}, {{"Personal fixture"}}, 1, FALSE, FALSE, TRUE, 1, 15, 1048576, 60, 0, 4, 2)
                """);
            await context.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "UserRoutingConfigurations" SET "SelectedProviderConfigurationId" = {{providerId}}
                WHERE "UserId" = {{legacyUserId}}
                """);
            await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($$"""
                DELETE FROM "RoutingProviderConfigurations" WHERE "Id" = {{providerId}}
                """));

            await using var first = fixture.CreateContext();
            await using var stale = fixture.CreateContext();
            var current = await first.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == legacyUserId);
            var staleCopy = await stale.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == legacyUserId);
            current.IncrementVersion();
            await first.SaveChangesAsync();
            staleCopy.IncrementVersion();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
            stale.ChangeTracker.Clear();
            Assert.Equal(current.ConfigurationVersion, (await stale.Set<UserRoutingConfiguration>().AsNoTracking()
                .SingleAsync(item => item.UserId == legacyUserId)).ConfigurationVersion);

            context.ChangeTracker.Clear();
            context.Users.Remove(await context.Users.SingleAsync(item => item.Id == legacyUserId));
            await context.SaveChangesAsync();
            Assert.False(await context.Set<UserRoutingConfiguration>().AnyAsync(item => item.UserId == legacyUserId));
        }
        finally
        {
            await migrator.MigrateAsync();
            await context.Database.ExecuteSqlInterpolatedAsync($$"""
                DELETE FROM "AspNetUsers" WHERE "Id" IN ({{legacyUserId}}, {{futureUserId}});
                DELETE FROM "RoutingProviderConfigurations" WHERE "Id" = {{providerId}};
                """);
        }
    }

    /// <summary>Proves provider/user races are rejected after unlocked contact with provider-first lock recovery.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verification_RacingProviderOrUserEdit_RejectsStaleWithOrderedLocks(bool changeProvider)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var providerId = Guid.NewGuid();
        var protection = new EphemeralDataProtectionProvider();
        int originalFeatureVersion;
        bool originalFeatureEnabled;
        uint expectedRowVersion;
        await using (var setup = fixture.CreateContext())
        {
            var settings = await setup.ApplicationSettings.SingleAsync(item => item.Id == 1);
            (originalFeatureEnabled, originalFeatureVersion) =
                (settings.ExternalRouteGenerationEnabled, settings.ExternalRouteGenerationVersion);
            settings.ExternalRouteGenerationEnabled = true;
            var profile = await setup.Set<TransportProfile>().FirstAsync(item => item.IsActive);
            var provider = VerificationProvider(providerId, profile);
            setup.Set<RoutingProviderConfiguration>().Add(provider);
            var configuration = await setup.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == user.Id);
            configuration.SelectPersonalProvider(providerId);
            new UserRoutingCredentialService(protection).Replace(configuration, providerId, "personal-secret");
            await setup.SaveChangesAsync();
            expectedRowVersion = configuration.RowVersion;
        }

        try
        {
            var recorder = new LockCommandRecorder();
            var transport = new BlockingValidTransport();
            await using var verificationContext = fixture.CreateContext(recorder);
            await using var mutationContext = fixture.CreateContext();
            var executor = new RoutingBoundedExecutor(new PublicResolver(),
                new RoutingEndpointPolicy(Options.Create(new RoutingOutboundOptions())), transport);
            var resolver = new AuthoritativeRoutingProviderResolver(verificationContext,
                new RoutingProviderCredentialService(protection), new UserRoutingCredentialService(protection));
            var service = new PersonalRoutingVerificationService(verificationContext, executor, resolver,
                new RoutingAttemptCoordinator(new RoutingProviderPacer(TimeProvider.System), new RoutingRequestBudget()));
            var pending = service.VerifyAsync(user.Id, expectedRowVersion, CancellationToken.None);
            await transport.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(recorder.Commands, command => command.Contains("FOR UPDATE", StringComparison.Ordinal));

            if (changeProvider)
            {
                var provider = await mutationContext.Set<RoutingProviderConfiguration>().SingleAsync(item => item.Id == providerId);
                provider.MarkConfigurationChanged();
            }
            else
            {
                var configuration = await mutationContext.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == user.Id);
                configuration.IncrementVersion();
            }
            await mutationContext.SaveChangesAsync();
            transport.Release();

            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("personal-routing-stale", result.ErrorCode);
            var providerLock = recorder.Commands.FindIndex(command => command.Contains("RoutingProviderConfigurations", StringComparison.Ordinal)
                && command.Contains("FOR UPDATE", StringComparison.Ordinal));
            var userLock = recorder.Commands.FindIndex(command => command.Contains("UserRoutingConfigurations", StringComparison.Ordinal)
                && command.Contains("FOR UPDATE", StringComparison.Ordinal));
            Assert.True(providerLock >= 0 && userLock > providerLock);
            Assert.Empty(verificationContext.ChangeTracker.Entries());
            Assert.Null((await verificationContext.Set<UserRoutingConfiguration>().AsNoTracking()
                .SingleAsync(item => item.UserId == user.Id)).VerifiedUserConfigurationVersion);
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            var configuration = await cleanup.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == user.Id);
            configuration.UseServerDefault();
            var settings = await cleanup.ApplicationSettings.SingleAsync(item => item.Id == 1);
            settings.ExternalRouteGenerationEnabled = originalFeatureEnabled;
            settings.ExternalRouteGenerationVersion = originalFeatureVersion;
            await cleanup.SaveChangesAsync();
            await cleanup.Set<RoutingProviderConfiguration>().Where(item => item.Id == providerId).ExecuteDeleteAsync();
        }
    }

    private static async Task InsertUserAsync(ApplicationDbContext context, string userId)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = userId, NormalizedUserName = userId.ToUpperInvariant(),
            DisplayName = "Routing fixture", IsActive = true
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static RoutingProviderConfiguration VerificationProvider(Guid id, TransportProfile profile)
    {
        var provider = new RoutingProviderConfiguration
        {
            Id = id, DisplayName = "Personal race fixture", BaseEndpoint = "https://routing.example",
            Enabled = true, PersonalRoutingAccess = PersonalRoutingAccess.CredentialRequired,
            ConfigurationVersion = 1, VerifiedConfigurationVersion = 1,
            Attribution = "Attribution", ExternalCoordinateDisclosure = "Coordinates leave Wayfarer.",
            VerificationFromLongitude = 23.7, VerificationFromLatitude = 37.9,
            VerificationToLongitude = 23.8, VerificationToLatitude = 38.0,
            MinimumIntervalMilliseconds = 0
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = id, TransportProfileId = profile.Id,
            TransportProfile = profile, OsrmProfile = "driving"
        });
        return provider;
    }

    private sealed class PublicResolver : IRoutingDnsResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("8.8.8.8")]);
    }

    private sealed class BlockingValidTransport : IRoutingPinnedTransport
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();
        public async Task<HttpResponseMessage> SendAsync(
            Uri requestUri, IPAddress selectedAddress, string? bearerCredential, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"code\":\"Ok\",\"routes\":[{\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[23.7,37.9],[23.8,38.0]]}}],\"waypoints\":[{\"location\":[23.7,37.9]},{\"location\":[23.8,38.0]}]}",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class LockCommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Commands.Add(command.CommandText); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
