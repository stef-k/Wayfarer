using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Areas.User.LocationProviderModels;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves legacy Mapbox migration shares the provider-settings row-lock order.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class LegacyMapboxMigrationLockOrderPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Concurrent migration and settings mutation converge without losing either credential.</summary>
    [PostgresFact]
    public async Task MigrationAndSaveProfile_UseSelectionBeforeProfileAndConverge()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id);
        var gate = new ReverseAcquisitionGate();
        await using var migrationContext = fixture.CreateContext(new MigrationLockInterceptor(gate));
        await using var settingsContext = fixture.CreateContext(new SettingsLockInterceptor(gate));
        var migrationCredentials = new PersonalProviderCredentialService(protection);
        var settingsCredentials = new PersonalProviderCredentialService(protection);
        var migration = new LegacyMapboxMigrationService(migrationContext, migrationCredentials);
        var controller = new LocationProviderSettingsController(
            settingsContext, settingsCredentials, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.Id)], "test"))
                }
            }
        };

        var migrate = migration.MigrateAsync(user.Id);
        var save = controller.SaveProfile(new LocationProviderProfileInput
        {
            ProviderKey = "mapbox",
            ReplacementCredential = "settings-mapbox-key",
            GeocodingAuthorized = true
        }, default);
        await Task.WhenAll(migrate, save).WaitAsync(TimeSpan.FromSeconds(10));
        var migrationResult = await migrate;

        await using var verify = fixture.CreateContext();
        var profile = await verify.PersonalLocationProviderProfiles.SingleAsync(
            item => item.UserId == user.Id && item.ProviderKey == "mapbox");
        var legacy = await verify.ApiTokens.IgnoreQueryFilters().Where(item => item.UserId == user.Id).ToListAsync();
        Assert.Equal("settings-mapbox-key", settingsCredentials.Read(profile).Credential);
        if (migrationResult.State == LegacyMapboxMigrationState.Migrated)
            Assert.Empty(legacy);
        else
        {
            Assert.Equal(LegacyMapboxMigrationState.Conflict, migrationResult.State);
            Assert.Equal("legacy-mapbox-key", Assert.Single(legacy).Token);
        }
        Assert.NotNull(await verify.PersonalLocationProviderSelections.SingleOrDefaultAsync(item => item.UserId == user.Id));
    }

    private async Task SeedAsync(string userId)
    {
        await using var context = fixture.CreateContext();
        var user = await context.Users.SingleAsync(item => item.Id == userId);
        context.Add(PersonalLocationProviderSelection.Create(userId));
        context.Add(PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Mapbox));
        context.ApiTokens.Add(new ApiToken
        {
            Name = "Mapbox", Token = "legacy-mapbox-key", UserId = userId, User = user
        });
        await context.SaveChangesAsync();
    }

    private sealed class ReverseAcquisitionGate
    {
        internal TaskCompletionSource MigrationFirstLock { get; } = NewSignal();
        internal TaskCompletionSource MigrationSelectionRequested { get; } = NewSignal();
        internal TaskCompletionSource MigrationSelectionAcquired { get; } = NewSignal();
        internal TaskCompletionSource SettingsSelectionAcquired { get; } = NewSignal();
        internal TaskCompletionSource MigrationSecondLockRequested { get; } = NewSignal();
        internal TaskCompletionSource SettingsSecondLockRequested { get; } = NewSignal();
        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class MigrationLockInterceptor(ReverseAcquisitionGate gate) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsSelectionQuery(command))
            {
                gate.MigrationSelectionRequested.TrySetResult();
                if (gate.MigrationFirstLock.Task.IsCompleted)
                {
                    gate.MigrationSecondLockRequested.TrySetResult();
                    await gate.SettingsSecondLockRequested.Task.WaitAsync(cancellationToken);
                }
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsSelectionLock(command)) gate.MigrationSelectionAcquired.TrySetResult();
            if (IsProfileLock(command) && !gate.MigrationSelectionAcquired.Task.IsCompleted)
            {
                gate.MigrationFirstLock.TrySetResult();
                await gate.SettingsSelectionAcquired.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class SettingsLockInterceptor(ReverseAcquisitionGate gate) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsProfileLock(command) && gate.SettingsSelectionAcquired.Task.IsCompleted)
            {
                gate.SettingsSecondLockRequested.TrySetResult();
                await gate.MigrationSecondLockRequested.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsSelectionLock(command))
            {
                var first = await Task.WhenAny(
                    gate.MigrationFirstLock.Task, gate.MigrationSelectionRequested.Task);
                if (first == gate.MigrationFirstLock.Task)
                {
                    gate.SettingsSelectionAcquired.TrySetResult();
                    await gate.MigrationFirstLock.Task.WaitAsync(cancellationToken);
                }
            }
            return result;
        }
    }

    private static bool IsSelectionLock(DbCommand command) =>
        IsSelectionQuery(command)
        && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);

    private static bool IsSelectionQuery(DbCommand command) =>
        command.CommandText.Contains("PersonalLocationProviderSelections", StringComparison.Ordinal);

    private static bool IsProfileLock(DbCommand command) =>
        command.CommandText.Contains("PersonalLocationProviderProfiles", StringComparison.Ordinal)
        && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);
}
