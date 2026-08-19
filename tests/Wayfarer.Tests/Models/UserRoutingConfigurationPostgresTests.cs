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
using Wayfarer.Areas.Admin.Models;
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
            foreach (var access in Enum.GetValues<PersonalRoutingAccess>())
            {
                await context.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE "RoutingProviderConfigurations" SET "PersonalRoutingAccess" = {{(int)access}}
                    WHERE "Id" = {{providerId}}
                    """);
                Assert.Equal(access, (await context.Set<RoutingProviderConfiguration>().AsNoTracking()
                    .SingleAsync(item => item.Id == providerId)).PersonalRoutingAccess);
            }
            foreach (var undefinedAccess in new[] { 999, -1 })
            {
                await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE "RoutingProviderConfigurations" SET "PersonalRoutingAccess" = {{undefinedAccess}}
                    WHERE "Id" = {{providerId}}
                    """));
                context.ChangeTracker.Clear();
                Assert.Equal(PersonalRoutingAccess.CredentialFree,
                    (await context.Set<RoutingProviderConfiguration>().AsNoTracking()
                        .SingleAsync(item => item.Id == providerId)).PersonalRoutingAccess);
                Assert.Equal(2, await context.Set<UserRoutingConfiguration>().CountAsync(item =>
                    item.UserId == legacyUserId || item.UserId == futureUserId));
            }
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
        SettingsSnapshot? settingsSnapshot = null;
        uint expectedRowVersion;

        try
        {
            await using (var setup = fixture.CreateContext())
            {
                var profile = await setup.Set<TransportProfile>().FirstAsync(item => item.IsActive);
                var provider = VerificationProvider(providerId, profile);
                setup.Set<RoutingProviderConfiguration>().Add(provider);
                var configuration = await setup.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == user.Id);
                configuration.SelectPersonalProvider(providerId);
                new UserRoutingCredentialService(protection).Replace(configuration, providerId, "personal-secret");
                await setup.SaveChangesAsync();
                settingsSnapshot = await EnableRoutingSettingsAsync(setup, providerId);
                expectedRowVersion = configuration.RowVersion;
            }

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
            await CleanupRoutingFixtureAsync(fixture, user.Id, providerId, settingsSnapshot);
        }
    }

    /// <summary>Proves a failed selecting-user cleanup rolls back the provider access transition.</summary>
    [PostgresFact]
    public async Task RequiredToCredentialFree_UserCleanupFailureRollsBackProviderAndUser()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var providerId = Guid.NewGuid();
        var protection = new EphemeralDataProtectionProvider();
        await using (var setup = fixture.CreateContext())
        {
            var profile = await setup.Set<TransportProfile>().FirstAsync(item => item.IsActive);
            var provider = VerificationProvider(providerId, profile);
            setup.Set<RoutingProviderConfiguration>().Add(provider);
            var configuration = await setup.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == user.Id);
            configuration.SelectPersonalProvider(providerId);
            new UserRoutingCredentialService(protection).Replace(configuration, providerId, "personal-secret");
            configuration.VerifiedUserConfigurationVersion = configuration.ConfigurationVersion;
            configuration.VerifiedProviderConfigurationVersion = provider.ConfigurationVersion;
            configuration.VerificationStatus = "verified";
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var mutation = fixture.CreateContext(new FailUserRoutingUpdateInterceptor());
            var provider = await mutation.Set<RoutingProviderConfiguration>().AsNoTracking()
                .Include(item => item.ProfileMappings).SingleAsync(item => item.Id == providerId);
            var model = new RoutingProviderEditViewModel
            {
                Id = provider.Id, DisplayName = provider.DisplayName, BaseEndpoint = provider.BaseEndpoint!,
                CredentialRequired = provider.CredentialRequired, CredentialPresent = provider.CredentialPresent,
                PersonalRoutingAccess = PersonalRoutingAccess.CredentialFree, Enabled = provider.Enabled,
                Attribution = provider.Attribution, ExternalCoordinateDisclosure = provider.ExternalCoordinateDisclosure!,
                VerificationFromLongitude = provider.VerificationFromLongitude,
                VerificationFromLatitude = provider.VerificationFromLatitude,
                VerificationToLongitude = provider.VerificationToLongitude,
                VerificationToLatitude = provider.VerificationToLatitude,
                GenerationTimeoutSeconds = provider.GenerationTimeoutSeconds,
                ResponseSizeLimitBytes = provider.ResponseSizeLimitBytes,
                RequestsPerMinute = provider.RequestsPerMinute, MaxConcurrency = provider.MaxConcurrency,
                MinimumIntervalSeconds = RoutingMinimumIntervalConverter.Format(provider.MinimumIntervalMilliseconds),
                RowVersion = provider.RowVersion, ConfigurationVersion = provider.ConfigurationVersion,
                Mappings = provider.ProfileMappings.Select(item => new RoutingProviderMappingViewModel
                    { TransportProfileId = item.TransportProfileId, OsrmProfile = item.OsrmProfile }).ToList()
            };
            var service = new RoutingProviderAdministrationService(mutation,
                new RoutingProviderCredentialService(protection), new RoutingProviderPacer(TimeProvider.System));

            var result = await service.SaveAsync(model, "admin", CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(mutation.ChangeTracker.Entries());
            await using var verification = fixture.CreateContext();
            var storedProvider = await verification.Set<RoutingProviderConfiguration>().AsNoTracking()
                .SingleAsync(item => item.Id == providerId);
            var storedUser = await verification.Set<UserRoutingConfiguration>().AsNoTracking()
                .SingleAsync(item => item.UserId == user.Id);
            Assert.Equal(PersonalRoutingAccess.CredentialRequired, storedProvider.PersonalRoutingAccess);
            Assert.True(storedUser.CredentialPresent);
            Assert.NotNull(storedUser.CredentialCiphertext);
        }
        finally
        {
            await CleanupRoutingFixtureAsync(fixture, user.Id, providerId, null);
        }
    }

    /// <summary>Proves admin cleanup and user replacement serialize under provider-first locking.</summary>
    [PostgresFact]
    public async Task RequiredToCredentialFree_RacingCredentialReplacementEndsCredentialFreeWithOrderedLocks()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var providerId = Guid.NewGuid();
        var protection = new EphemeralDataProtectionProvider();
        SettingsSnapshot? settingsSnapshot = null;
        uint expectedUserRowVersion;
        var originalProviderVersion = 0;

        try
        {
            await using (var setup = fixture.CreateContext())
            {
                var profile = await setup.Set<TransportProfile>().FirstAsync(item => item.IsActive);
                var configuredProvider = VerificationProvider(providerId, profile);
                setup.Set<RoutingProviderConfiguration>().Add(configuredProvider);
                var configuration = await setup.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == user.Id);
                configuration.SelectPersonalProvider(providerId);
                new UserRoutingCredentialService(protection).Replace(configuration, providerId, "first-secret");
                await setup.SaveChangesAsync();
                settingsSnapshot = await EnableRoutingSettingsAsync(setup, providerId);
                expectedUserRowVersion = configuration.RowVersion;
                originalProviderVersion = configuredProvider.ConfigurationVersion;
            }

            await using var adminGate = new ProviderLockGateInterceptor(holdAfterAcquisition: true);
            await using var userGate = new ProviderLockGateInterceptor(holdAfterAcquisition: false);
            await using var adminContext = fixture.CreateContext(adminGate);
            await using var userContext = fixture.CreateContext(userGate);
            await using var observerContext = fixture.CreateContext();
            var provider = await adminContext.Set<RoutingProviderConfiguration>().AsNoTracking()
                .Include(item => item.ProfileMappings).SingleAsync(item => item.Id == providerId);
            var administration = new RoutingProviderAdministrationService(adminContext,
                new RoutingProviderCredentialService(protection), new RoutingProviderPacer(TimeProvider.System));
            var userService = new UserRoutingConfigurationService(userContext,
                new UserRoutingCredentialService(protection));

            var adminTask = administration.SaveAsync(
                CredentialFreeModel(provider), "admin", CancellationToken.None);
            await adminGate.LockAcquired.WaitAsync(TimeSpan.FromSeconds(10));
            var userTask = userService.SaveAsync(
                user.Id, providerId, "replacement-secret", expectedUserRowVersion, CancellationToken.None);
            await userGate.LockRequested.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(adminGate.BackendProcessId > 0);
            Assert.True(userGate.BackendProcessId > 0);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await ObserveBlockedProviderLockAsync(observerContext, userGate.BackendProcessId,
                    adminGate.BackendProcessId, timeout.Token);
            adminGate.Release();
            await Task.WhenAll(adminTask, userTask).WaitAsync(TimeSpan.FromSeconds(15));
            var adminResult = await adminTask;
            var userResult = await userTask;

            Assert.True(adminResult.Succeeded);
            Assert.Equal(UserRoutingMutationResult.Conflict, userResult);
            AssertProviderBeforeUser(adminGate.Commands);
            Assert.Contains(userGate.Commands, command => command.Contains("RoutingProviderConfigurations", StringComparison.Ordinal)
                && command.Contains("FOR UPDATE", StringComparison.Ordinal));
            Assert.Empty(userContext.ChangeTracker.Entries());
            Assert.Equal(PersonalRoutingAccess.CredentialFree,
                (await userContext.Set<RoutingProviderConfiguration>().AsNoTracking()
                    .SingleAsync(item => item.Id == providerId)).PersonalRoutingAccess);
            var recoveredUser = await userContext.Set<UserRoutingConfiguration>().AsNoTracking()
                .SingleAsync(item => item.UserId == user.Id);
            Assert.False(recoveredUser.CredentialPresent);
            Assert.Null(recoveredUser.CredentialCiphertext);
            Assert.Null(recoveredUser.VerifiedUserConfigurationVersion);
            Assert.Null(recoveredUser.VerifiedProviderConfigurationVersion);
            await using var verification = fixture.CreateContext();
            var storedProvider = await verification.Set<RoutingProviderConfiguration>().AsNoTracking()
                .SingleAsync(item => item.Id == providerId);
            var storedUser = await verification.Set<UserRoutingConfiguration>().AsNoTracking()
                .SingleAsync(item => item.UserId == user.Id);
            Assert.Equal(PersonalRoutingAccess.CredentialFree, storedProvider.PersonalRoutingAccess);
            Assert.Equal(providerId, storedUser.SelectedProviderConfigurationId);
            Assert.False(storedUser.CredentialPresent);
            Assert.Null(storedUser.CredentialCiphertext);
            Assert.Null(storedUser.VerifiedUserConfigurationVersion);
            Assert.Null(storedUser.VerifiedProviderConfigurationVersion);
            Assert.True(storedProvider.ConfigurationVersion > originalProviderVersion);
        }
        finally
        {
            await CleanupRoutingFixtureAsync(fixture, user.Id, providerId, settingsSnapshot);
        }
    }

    private static async Task ObserveBlockedProviderLockAsync(
        ApplicationDbContext observerContext, int backendProcessId, int blockingProcessId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var blocked = await observerContext.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {0} "
                + "AND wait_event_type = 'Lock' AND {1} = ANY(pg_blocking_pids(pid))) AS \"Value\"",
                backendProcessId, blockingProcessId).SingleAsync(cancellationToken);
            if (blocked) return;
            await Task.Yield();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<SettingsSnapshot> EnableRoutingSettingsAsync(
        ApplicationDbContext context, Guid providerId)
    {
        var settings = await context.ApplicationSettings.SingleOrDefaultAsync(item => item.Id == 1);
        var snapshot = new SettingsSnapshot(settings == null,
            settings == null ? null : (ApplicationSettings)context.Entry(settings).CurrentValues.ToObject());
        settings ??= new ApplicationSettings { Id = 1 };
        if (snapshot.Created) context.ApplicationSettings.Add(settings);
        settings.ExternalRouteGenerationEnabled = true;
        settings.ExternalRouteGenerationVersion = Math.Max(1, settings.ExternalRouteGenerationVersion);
        settings.ActiveRoutingProviderConfigurationId = providerId;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return snapshot;
    }

    private static async Task CleanupRoutingFixtureAsync(
        PostgresImportTestFixture fixture, string userId, Guid providerId, SettingsSnapshot? snapshot)
    {
        await using var cleanup = fixture.CreateContext();
        cleanup.ChangeTracker.Clear();
        var settings = await cleanup.ApplicationSettings.SingleOrDefaultAsync(item => item.Id == 1);
        if (snapshot is { Created: true } && settings != null) cleanup.ApplicationSettings.Remove(settings);
        else if (snapshot?.Original != null && settings != null)
            cleanup.Entry(settings).CurrentValues.SetValues(snapshot.Original);
        await cleanup.SaveChangesAsync();
        cleanup.ChangeTracker.Clear();
        var configuration = await cleanup.Set<UserRoutingConfiguration>()
            .SingleOrDefaultAsync(item => item.UserId == userId);
        if (configuration != null) { configuration.UseServerDefault(); await cleanup.SaveChangesAsync(); }
        await cleanup.AuditLogs.Where(item => item.UserId == userId
            || item.Details.Contains(providerId.ToString())).ExecuteDeleteAsync();
        await cleanup.Users.Where(item => item.Id == userId).ExecuteDeleteAsync();
        await cleanup.Set<RoutingProviderConfiguration>().Where(item => item.Id == providerId).ExecuteDeleteAsync();
        cleanup.ChangeTracker.Clear();
    }

    private sealed record SettingsSnapshot(bool Created, ApplicationSettings? Original);

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

    private static RoutingProviderEditViewModel CredentialFreeModel(RoutingProviderConfiguration provider) => new()
    {
        Id = provider.Id, DisplayName = provider.DisplayName, BaseEndpoint = provider.BaseEndpoint!,
        CredentialRequired = provider.CredentialRequired, CredentialPresent = provider.CredentialPresent,
        PersonalRoutingAccess = PersonalRoutingAccess.CredentialFree, Enabled = provider.Enabled,
        Attribution = provider.Attribution, ExternalCoordinateDisclosure = provider.ExternalCoordinateDisclosure!,
        VerificationFromLongitude = provider.VerificationFromLongitude,
        VerificationFromLatitude = provider.VerificationFromLatitude,
        VerificationToLongitude = provider.VerificationToLongitude,
        VerificationToLatitude = provider.VerificationToLatitude,
        GenerationTimeoutSeconds = provider.GenerationTimeoutSeconds,
        ResponseSizeLimitBytes = provider.ResponseSizeLimitBytes,
        RequestsPerMinute = provider.RequestsPerMinute, MaxConcurrency = provider.MaxConcurrency,
        MinimumIntervalSeconds = RoutingMinimumIntervalConverter.Format(provider.MinimumIntervalMilliseconds),
        RowVersion = provider.RowVersion, ConfigurationVersion = provider.ConfigurationVersion,
        Mappings = provider.ProfileMappings.Select(item => new RoutingProviderMappingViewModel
            { TransportProfileId = item.TransportProfileId, OsrmProfile = item.OsrmProfile }).ToList()
    };

    private static void AssertProviderBeforeUser(IReadOnlyList<string> commands)
    {
        var providerLock = commands.ToList().FindIndex(command =>
            command.Contains("RoutingProviderConfigurations", StringComparison.Ordinal)
            && command.Contains("FOR UPDATE", StringComparison.Ordinal));
        var userLock = commands.ToList().FindIndex(command =>
            command.Contains("UserRoutingConfigurations", StringComparison.Ordinal)
            && command.Contains("FOR UPDATE", StringComparison.Ordinal));
        Assert.True(providerLock >= 0 && userLock > providerLock);
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

    /// <summary>Observes provider-lock requests and optionally holds an acquired production lock.</summary>
    private sealed class ProviderLockGateInterceptor(bool holdAfterAcquisition) : DbCommandInterceptor, IAsyncDisposable
    {
        private readonly TaskCompletionSource _lockRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _lockAcquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Commands { get; } = [];
        public Task LockRequested => _lockRequested.Task;
        public Task LockAcquired => _lockAcquired.Task;
        public int BackendProcessId { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (IsProviderLock(command))
            {
                BackendProcessId = ((NpgsqlConnection)command.Connection!).ProcessID;
                _lockRequested.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (holdAfterAcquisition && IsProviderLock(command))
            {
                _lockAcquired.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public void Release() => _release.TrySetResult();
        public ValueTask DisposeAsync() { Release(); return ValueTask.CompletedTask; }

        private static bool IsProviderLock(DbCommand command) =>
            command.CommandText.Contains("RoutingProviderConfigurations", StringComparison.Ordinal)
            && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal);
    }

    private sealed class FailUserRoutingUpdateInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowForUserUpdate(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowForUserUpdate(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowForUserUpdate(command);
            return ValueTask.FromResult(result);
        }

        private static void ThrowForUserUpdate(DbCommand command)
        {
            if (command.CommandText.Contains("UPDATE \"UserRoutingConfigurations\"", StringComparison.Ordinal))
                throw new DbUpdateConcurrencyException("Injected user cleanup conflict.");
        }
    }
}
