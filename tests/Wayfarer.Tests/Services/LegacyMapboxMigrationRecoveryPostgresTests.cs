using System.Data.Common;
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
    /// <summary>Two first-time migrations finish without losing recovery state; a fresh retry converges.</summary>
    [PostgresFact]
    public async Task ConcurrentFirstMigration_ConvergesWithoutDuplicateAuthority()
    {
        var user = await SeedAsync();
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var gate = new FirstProfileReadGate();
        await using var first = fixture.CreateContext(gate);
        await using var second = fixture.CreateContext();
        var firstTask = new LegacyMapboxMigrationService(first, credentials).MigrateAsync(user.Id, deadline.Token);
        await gate.Reached.Task.WaitAsync(deadline.Token);
        try
        {
            // The first transaction has observed no profile but has not locked legacy rows yet.
            await new LegacyMapboxMigrationService(second, credentials).MigrateAsync(user.Id, deadline.Token);
        }
        finally { gate.Release.TrySetResult(); }
        await firstTask.WaitAsync(deadline.Token);
        await AssertConvergedAndRetryAsync(user.Id, credentials);
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
