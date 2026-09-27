using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Areas.User.LocationProviderModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies absent-row concurrency and transactional recovery at the migration owner.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class LegacyMapboxMigrationRecoveryPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Navigation preserves durable legacy, consent, verification, authorization and selection fields.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsNavigation_PreservesDurableProviderAndLegacyState(bool pending)
    {
        var user = await SeedAsync();
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await using (var seed = fixture.CreateContext())
        {
            if (!pending)
                (await seed.ApiTokens.IgnoreQueryFilters().SingleAsync(p => p.UserId == user.Id)).Token = "\t\u00a0\u3000";
            var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Mapbox);
            credentials.Replace(profile, "legacy-recovery-sentinel");
            profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
            profile.SetAuthorization(PersonalProviderCapability.Routing, true);
            profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
            credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
            var selection = PersonalLocationProviderSelection.Create(user.Id);
            selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Mapbox);
            seed.AddRange(profile, selection);
            await seed.SaveChangesAsync();
        }
        var before = await DurableSnapshotAsync(user.Id);
        await using (var context = fixture.CreateContext())
        {
            var controller = new LocationProviderSettingsController(context, credentials,
                new LegacyMapboxMigrationService(context, credentials), null!)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "test"))
                    }
                }
            };
            var view = Assert.IsType<ViewResult>(await controller.Index(default));
            var model = Assert.IsType<LocationProviderSettingsViewModel>(view.Model);
            Assert.Equal(pending, model.HasLegacyMapboxRows);
            Assert.Equal("Ready with mapbox.", model.GeocodingStatus);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        Assert.Equal(before, await DurableSnapshotAsync(user.Id));
    }

    /// <summary>Reads every persisted authority field and legacy row through a fresh PostgreSQL context.</summary>
    private async Task<string> DurableSnapshotAsync(string userId)
    {
        await using var context = fixture.CreateContext();
        return JsonSerializer.Serialize(new
        {
            Profiles = await context.PersonalLocationProviderProfiles.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(),
            Selections = await context.PersonalLocationProviderSelections.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(),
            Legacy = await context.ApiTokens.IgnoreQueryFilters().AsNoTracking().Where(p => p.UserId == userId)
                .Select(p => new { p.Id, p.Name, p.Token, p.TokenHash }).ToListAsync()
        });
    }

    /// <summary>Two first-time migrations finish without losing recovery state; a fresh retry converges.</summary>
    [PostgresFact]
    public async Task ConcurrentFirstMigration_ConvergesWithoutDuplicateAuthority()
    {
        var user = await SeedAsync();
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var gate = new FirstProfileReadGate();
        await using var first = fixture.CreateContext(gate);
        await using var second = fixture.CreateContext(new CompetingMigrationGate(gate));
        var firstTask = new LegacyMapboxMigrationService(first, credentials).MigrateAsync(user.Id, deadline.Token);
        await gate.Reached.Task.WaitAsync(deadline.Token);
        try
        {
            // The competitor either waits on absent-row serialization or exposes the old stale-profile race.
            await new LegacyMapboxMigrationService(second, credentials).MigrateAsync(user.Id, deadline.Token);
        }
        finally { gate.Release.TrySetResult(); }
        await firstTask.WaitAsync(deadline.Token);
        await AssertConvergedAndRetryAsync(user.Id, credentials);
    }

    /// <summary>Concurrent first assessment of conflicting rows must retain both copies without duplicate profiles.</summary>
    [PostgresFact]
    public async Task ConcurrentFirstConflict_PreservesRecoveryCopies()
    {
        var user = await SeedAsync();
        await using (var seed = fixture.CreateContext())
        {
            seed.ApiTokens.Add(new ApiToken
            {
                UserId = user.Id, User = await seed.Users.SingleAsync(p => p.Id == user.Id),
                Name = "Mapbox", Token = "different-recovery-sentinel"
            });
            await seed.SaveChangesAsync();
        }
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var gate = new FirstProfileReadGate();
        await using var first = fixture.CreateContext(gate);
        await using var second = fixture.CreateContext(new CompetingMigrationGate(gate));
        var firstTask = new LegacyMapboxMigrationService(first, credentials).MigrateAsync(user.Id, deadline.Token);
        await gate.Reached.Task.WaitAsync(deadline.Token);
        try { await new LegacyMapboxMigrationService(second, credentials).MigrateAsync(user.Id, deadline.Token); }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(LegacyMapboxMigrationState.Conflict, (await firstTask.WaitAsync(deadline.Token)).State);
        await using var retry = fixture.CreateContext();
        Assert.Equal(LegacyMapboxMigrationState.Conflict,
            (await new LegacyMapboxMigrationService(retry, credentials).MigrateAsync(user.Id)).State);
        await using var verify = fixture.CreateContext();
        Assert.Single(await verify.PersonalLocationProviderProfiles.Where(p => p.UserId == user.Id).ToListAsync());
        Assert.Equal(2, await verify.ApiTokens.IgnoreQueryFilters().CountAsync(p => p.UserId == user.Id));
        Assert.Empty(await verify.PersonalLocationProviderSelections.Where(p => p.UserId == user.Id).ToListAsync());
    }

    /// <summary>Cancellation after actual protected persistence rolls back, retaining plaintext for a fresh retry.</summary>
    [PostgresFact]
    public async Task InterruptedPersistence_RollsBackAndFreshRetryConverges()
    {
        var user = await SeedAsync();
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        using var cancellation = new CancellationTokenSource();
        var interrupt = new InterruptAfterProfileSave(cancellation);
        await using (var context = fixture.CreateContext(interrupt))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new LegacyMapboxMigrationService(context, credentials).MigrateAsync(user.Id, cancellation.Token));
        Assert.True(interrupt.Interrupted);
        await using (var verify = fixture.CreateContext())
        {
            Assert.Empty(await verify.PersonalLocationProviderProfiles.Where(p => p.UserId == user.Id).ToListAsync());
            Assert.Empty(await verify.PersonalLocationProviderSelections.Where(p => p.UserId == user.Id).ToListAsync());
            Assert.Equal("legacy-recovery-sentinel", (await verify.ApiTokens.IgnoreQueryFilters()
                .SingleAsync(p => p.UserId == user.Id)).Token);
        }
        await AssertConvergedAndRetryAsync(user.Id, credentials);
    }

    /// <summary>Creates only legacy rows, leaving profile and selection absent.</summary>
    private async Task<ApplicationUser> SeedAsync()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        await using var context = fixture.CreateContext();
        context.ApiTokens.Add(new ApiToken
        {
            UserId = user.Id, User = await context.Users.SingleAsync(p => p.Id == user.Id),
            Name = " MapBox ", Token = "legacy-recovery-sentinel"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var migration = new LegacyMapboxMigrationService(context,
            CredentialTestFactory.Create(new EphemeralDataProtectionProvider()));
        Assert.True(await migration.HasLegacyRowsAsync(user.Id));
        Assert.Empty(context.ChangeTracker.Entries());
        return user;
    }

    /// <summary>Checks last-readable-copy preservation and two committed retries through fresh contexts.</summary>
    private async Task AssertConvergedAndRetryAsync(string userId, PersonalProviderCredentialService credentials)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = fixture.CreateContext();
            var result = await new LegacyMapboxMigrationService(context, credentials).MigrateAsync(userId);
            Assert.Equal(LegacyMapboxMigrationState.Migrated, result.State);
            Assert.False(await new LegacyMapboxMigrationService(context, credentials).HasLegacyRowsAsync(userId));
            Assert.True(result.ProtectedCredentialReady);
            await using var verify = fixture.CreateContext();
            var profile = await verify.PersonalLocationProviderProfiles.SingleAsync(p => p.UserId == userId);
            Assert.Equal("legacy-recovery-sentinel", credentials.Read(profile).Credential);
            Assert.Empty(await verify.ApiTokens.IgnoreQueryFilters().Where(p => p.UserId == userId).ToListAsync());
            Assert.Empty(await verify.PersonalLocationProviderSelections.Where(p => p.UserId == userId).ToListAsync());
            Assert.False(profile.RoutingAuthorized);
            Assert.False(profile.HasCurrentPermanentGeocodingConsent());
            Assert.Equal(PersonalProviderVerification.Unverified, profile.GeocodingVerification);
        }
    }

    /// <summary>Pauses after PostgreSQL executed the initial absent profile query, before legacy locking.</summary>
    private sealed class FirstProfileReadGate : DbCommandInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PersonalLocationProviderProfiles") && command.CommandText.Contains("FOR UPDATE")
                && Reached.TrySetResult())
                await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    /// <summary>Releases the first attempt when the competitor requests serialization, or has read a stale absent profile.</summary>
    private sealed class CompetingMigrationGate(FirstProfileReadGate first) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock")) first.Release.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PersonalLocationProviderProfiles") && command.CommandText.Contains("FOR UPDATE"))
                first.Release.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Interrupts only after the first protected profile INSERT has really completed inside the transaction.</summary>
    private sealed class InterruptAfterProfileSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        internal bool Interrupted { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PersonalLocationProviderProfile>()
                .Any(entry => entry.Entity.StableProtectedCredential != null))
            {
                Interrupted = true;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }
}
