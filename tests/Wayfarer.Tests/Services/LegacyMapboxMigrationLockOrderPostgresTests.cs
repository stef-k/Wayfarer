using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Areas.User.LocationProviderModels;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises legacy Mapbox migration alongside concurrent provider settings changes.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class LegacyMapboxMigrationLockOrderPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Distinct plaintext token shared by both migration fixtures.</summary>
    private const string LegacyToken = "legacy-mapbox-key";

    /// <summary>Concurrent migration and replacement preserve the replacement and account for the legacy token.</summary>
    [PostgresFact]
    public Task MigrationAndCredentialReplacement_ConvergeWithoutLosingCredentials() =>
        WithFixtureUserAsync(async userId =>
        {
            const string replacement = "settings-mapbox-key";
            var protection = new EphemeralDataProtectionProvider();
            await SeedAsync(PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Mapbox));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var migrationContext = fixture.CreateContext();
            await using var settingsContext = fixture.CreateContext();
            var migrationCredentials = CredentialTestFactory.Create(protection);
            var settingsCredentials = CredentialTestFactory.Create(protection);
            var migration = new LegacyMapboxMigrationService(migrationContext, migrationCredentials);
            var controller = new LocationProviderSettingsController(
                settingsContext, settingsCredentials, null!, null!,
                setup: new PersonalProviderSetupService(settingsContext, settingsCredentials))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
                    }
                }
            };
            // Direct controller calls bypass MVC's TempData setup for the redirect message.
            controller.TempData = new TempDataDictionary(controller.HttpContext, Mock.Of<ITempDataProvider>());

            var migrate = migration.MigrateAsync(userId, deadline.Token);
            var save = controller.SaveProfile(new LocationProviderProfileInput
            {
                ProviderKey = "mapbox",
                ReplacementCredential = replacement,
                GeocodingAuthorized = true
            }, deadline.Token);
            await Task.WhenAll(migrate, save);

            await using var verify = fixture.CreateContext();
            var profile = await verify.PersonalLocationProviderProfiles.SingleAsync(
                item => item.UserId == userId && item.ProviderKey == "mapbox");
            var legacy = await verify.ApiTokens.IgnoreQueryFilters().Where(item => item.UserId == userId).ToListAsync();
            Assert.Equal(replacement, settingsCredentials.Read(profile).Credential);
            var migrationResult = await migrate;
            if (migrationResult.State == LegacyMapboxMigrationState.Migrated)
                Assert.Empty(legacy);
            else
            {
                Assert.Equal(LegacyMapboxMigrationState.Conflict, migrationResult.State);
                Assert.Equal(LegacyToken, Assert.Single(legacy).Token);
            }
            Assert.NotNull(await verify.PersonalLocationProviderSelections.SingleOrDefaultAsync(item => item.UserId == userId));
            output.WriteLine($"Credential replacement persisted. Migration result: {migrationResult.State}.");
        });

    /// <summary>Migration and an explicit Mapbox choice contend on the same selection, then lock the same profile.</summary>
    [PostgresFact]
    public Task MigrationAndExplicitMapboxChoice_LockSelectionBeforeProfileAndPreserveConflict() =>
        WithFixtureUserAsync(async userId =>
        {
            const string existingCredential = "protected-mapbox-key";
            var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
            var seededProfile = PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Mapbox);
            credentials.Replace(seededProfile, existingCredential);
            seededProfile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
            seededProfile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
            credentials.RecordVerification(seededProfile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
            await SeedAsync(seededProfile);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var token = deadline.Token;
            var releaseMigration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var migrationLocks = new ProviderLockInterceptor(releaseMigration.Task);
            var choiceLocks = new ProviderLockInterceptor();
            await using var migrationContext = fixture.CreateContext(migrationLocks);
            await using var choiceContext = fixture.CreateContext(choiceLocks);
            var migration = new LegacyMapboxMigrationService(migrationContext, credentials);
            var setup = new PersonalProviderSetupService(choiceContext, credentials);

            // Hold the first acquired row until PostgreSQL confirms the second transaction is waiting on it.
            var migrate = migration.MigrateAsync(userId, token);
            var choose = ChooseAfterMigrationLocksAsync();
            await Task.WhenAll(ObserveContentionAsync(), migrate, choose);

            output.WriteLine($"Migration FOR UPDATE: {string.Join(" → ", migrationLocks.Acquired)}");
            output.WriteLine($"Provider choice FOR UPDATE: {string.Join(" → ", choiceLocks.Acquired)}");
            Assert.Equal(["selection", "profile"], migrationLocks.Acquired);
            Assert.Equal(["selection", "profile"], choiceLocks.Acquired);
            Assert.Equal(ProviderChoiceResult.Success, await choose);
            Assert.Equal(LegacyMapboxMigrationState.Conflict, (await migrate).State);

            await using var verify = fixture.CreateContext();
            var selection = await verify.PersonalLocationProviderSelections.SingleAsync(item => item.UserId == userId);
            var profile = await verify.PersonalLocationProviderProfiles.SingleAsync(
                item => item.UserId == userId && item.ProviderKey == "mapbox");
            var legacy = await verify.ApiTokens.IgnoreQueryFilters().SingleAsync(item => item.UserId == userId);
            Assert.Equal("mapbox", selection.GeocodingProviderKey);
            Assert.Equal(LegacyMapboxMigrationState.Conflict, profile.LegacyMigrationState);
            Assert.Equal(seededProfile.StableProtectedCredential, profile.StableProtectedCredential);
            Assert.Equal(existingCredential, credentials.Read(profile).Credential);
            Assert.Equal(LegacyToken, legacy.Token);

            // Start selection only once migration can hold its first row lock open.
            async Task<ProviderChoiceResult> ChooseAfterMigrationLocksAsync()
            {
                await migrationLocks.FirstAcquired.Task.WaitAsync(token);
                return await setup.ChooseAsync(userId, PersonalProviderCapability.Geocoding, PersonalLocationProvider.Mapbox, token);
            }

            // Always release migration so both service tasks can settle before their contexts are disposed.
            async Task ObserveContentionAsync()
            {
                try
                {
                    var held = await migrationLocks.FirstAcquired.Task.WaitAsync(token);
                    Assert.Equal("selection", held.Row);
                    var waiting = await choiceLocks.FirstRequested.Task.WaitAsync(token);
                    Assert.Equal("selection", waiting.Row);
                    Assert.NotEqual(held.BackendId, waiting.BackendId);
                    await WaitForBlockAsync(waiting.BackendId, held.BackendId, token);
                    output.WriteLine("PostgreSQL confirmed provider choice waited on migration's selection lock.");
                }
                finally
                {
                    releaseMigration.TrySetResult();
                }
            }
        });

    /// <summary>Uses PostgreSQL's lock graph to distinguish a blocked command from one merely approaching the server.</summary>
    private async Task WaitForBlockAsync(int waitingBackend, int holdingBackend, CancellationToken cancellationToken)
    {
        await using var observer = fixture.CreateConnection();
        await observer.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", observer);
        command.Parameters.AddWithValue("holder", holdingBackend);
        command.Parameters.AddWithValue("waiter", waitingBackend);
        while (await command.ExecuteScalarAsync(cancellationToken) is not true)
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
    }

    /// <summary>Seeds persisted rows for both operations to lock, with no provider selected initially.</summary>
    private async Task SeedAsync(PersonalLocationProviderProfile profile)
    {
        await using var context = fixture.CreateContext();
        var user = await context.Users.SingleAsync(item => item.Id == profile.UserId);
        context.Add(PersonalLocationProviderSelection.Create(user.Id));
        context.Add(profile);
        context.ApiTokens.Add(new ApiToken
        {
            Name = "Mapbox", Token = LegacyToken, UserId = user.Id, User = user
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Cleans each case's user and dependent rows while preserving any primary test failure.</summary>
    private async Task WithFixtureUserAsync(Func<string, Task> test)
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        Exception? primary = null;
        try
        {
            await test(user.Id);
        }
        catch (Exception exception)
        {
            primary = exception;
        }
        finally
        {
            await FailureIndependentCleanup.CompleteAsync(primary,
            [
                ("relational fixture cleanup", async () =>
                {
                    await using var cleanup = fixture.CreateContext();
                    await cleanup.Users.Where(item => item.Id == user.Id).ExecuteDeleteAsync();
                })
            ]);
        }
    }

    /// <summary>Records provider row locks in one transaction and optionally pauses after the first acquisition.</summary>
    private sealed class ProviderLockInterceptor(Task? releaseFirstLock = null) : DbCommandInterceptor
    {
        /// <summary>Signals the first provider row command before it is sent.</summary>
        internal TaskCompletionSource<(string Row, int BackendId)> FirstRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signals the first provider row lock after PostgreSQL acquires it.</summary>
        internal TaskCompletionSource<(string Row, int BackendId)> FirstAcquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lists provider row locks in the order PostgreSQL returned them.</summary>
        internal List<string> Acquired { get; } = [];

        /// <summary>Signals the first provider lock request before the command reaches PostgreSQL.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!FirstRequested.Task.IsCompleted && ProviderRowLock(command) is { } row)
                FirstRequested.TrySetResult((row, BackendId(command)));
            return ValueTask.FromResult(result);
        }

        /// <summary>Records acquisition after PostgreSQL returns the reader for a locking query.</summary>
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (ProviderRowLock(command) is { } row)
            {
                Acquired.Add(row);
                if (FirstAcquired.TrySetResult((row, BackendId(command))) && releaseFirstLock is not null)
                    await releaseFirstLock.WaitAsync(cancellationToken);
            }
            return result;
        }

        /// <summary>Gets the PostgreSQL backend for a command participating in the service transaction.</summary>
        private static int BackendId(DbCommand command)
        {
            Assert.NotNull(command.Transaction);
            return Assert.IsType<NpgsqlConnection>(command.Connection).ProcessID;
        }

        /// <summary>Identifies locking reads of the two provider authority rows.</summary>
        private static string? ProviderRowLock(DbCommand command)
        {
            if (!command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)) return null;
            if (command.CommandText.Contains("\"PersonalLocationProviderSelections\"", StringComparison.Ordinal)) return "selection";
            if (command.CommandText.Contains("\"PersonalLocationProviderProfiles\"", StringComparison.Ordinal)) return "profile";
            return null;
        }
    }
}
