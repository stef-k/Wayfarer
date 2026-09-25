using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wayfarer.CommandLine;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Real migration, xmin, transactional failure, command and writer-exclusion qualification.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class StableIdentityPreparationPostgresTests
{
    /// <summary>Preparation changes only companions/xmin, reruns without writes, and stays ready after ordinary replacement.</summary>
    [PostgresFact]
    public async Task PreparationAndStatus_PreserveAuthorityAndSupportIdempotentRollback()
    {
        await using var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        using var directory = new TestDirectory();
        await using var host = StableIdentityCryptographyTests.Host(directory.Path, Path.Combine(directory.Path, "ring"));
        var provider = host.Services.GetRequiredService<IDataProtectionProvider>();
        var owner = host.Services.GetRequiredService<PersonalProviderCredentialService>();
        var codec = new LegacyCredentialPreparationCodec(provider, new StableDataProtectionProvider(provider));
        await SeedAsync(fixture, owner, provider);
        await using var db = fixture.CreateContext();
        var before = await db.PersonalLocationProviderProfiles.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        var preparation = new StableIdentityPreparation(db, codec);
        var pending = await preparation.StatusAsync();
        Assert.Equal(new StableIdentityStatus(3, 1, 2, 0, 1), pending);
        Assert.Equal(1, await RunCliAsync("status", preparation));
        var ready = await preparation.PrepareAsync();
        Assert.True(ready.Ready);
        var after = await db.PersonalLocationProviderProfiles.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        AssertOnlyCompanionsChanged(db, before, after, successful: true);
        Assert.All(after.Where(row => row.ProtectedCredential != null), row =>
        {
            Assert.True(owner.Read(row).Succeeded);
            Assert.True(codec.ReadStable(row).Succeeded);
        });
        await using var rerunDb = fixture.CreateContext();
        Assert.Equal(0, await RunCliAsync("prepare-stable-identity", new StableIdentityPreparation(rerunDb, codec)));
        var rerun = await db.PersonalLocationProviderProfiles.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        AssertOnlyCompanionsChanged(db, after, rerun, successful: false);

        // An old reader's xmin cannot overwrite the newly prepared companion.
        await using var oldWriter = fixture.CreateContext();
        var stale = before.First(row => row.ProtectedCredential != null && row.StableProtectedCredential == null);
        oldWriter.Attach(stale);
        stale.RoutingAuthorized = !stale.RoutingAuthorized;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => oldWriter.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var replacement = await db.PersonalLocationProviderProfiles.FirstAsync(row => row.ProtectedCredential != null);
        owner.Replace(replacement, "post-preparation-replacement");
        await db.SaveChangesAsync();
        Assert.True((await new StableIdentityReadiness(db, owner).StatusAsync()).Ready);
        Assert.Null(replacement.ProtectedCredential);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var column = new NpgsqlCommand("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_name = 'PersonalLocationProviderProfiles' AND column_name = 'StableProtectedCredential'
            AND is_nullable = 'YES' AND character_maximum_length = 4096
            """, connection);
        Assert.Equal(1L, await column.ExecuteScalarAsync());
    }

    /// <summary>Every conflicting state and a late protection/save failure leave the entire selected set untouched.</summary>
    [PostgresTheory]
    [InlineData("legacy")]
    [InlineData("stable")]
    [InlineData("mismatch")]
    [InlineData("stable-only")]
    [InlineData("protection")]
    [InlineData("save")]
    public async Task Failure_RollsBackEveryCompanionAndRedactsCommand(string failure)
    {
        await using var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        var provider = new EphemeralDataProtectionProvider();
        var owner = CredentialTestFactory.Create(provider);
        var codec = new LegacyCredentialPreparationCodec(provider, new StableDataProtectionProvider(provider));
        await SeedAsync(fixture, owner, provider);
        await using (var corrupt = fixture.CreateContext())
        {
            var row = await corrupt.PersonalLocationProviderProfiles.FirstAsync(item => item.StableProtectedCredential != null);
            if (failure == "legacy") row.ProtectedCredential = StableIdentityCryptographyTests.Secret;
            if (failure == "stable") row.StableProtectedCredential = StableIdentityCryptographyTests.Secret;
            if (failure == "stable-only") row.ProtectedCredential = null;
            if (failure == "mismatch")
            {
                var other = PersonalLocationProviderProfile.Create(row.UserId, PersonalLocationProvider.Mapbox);
                owner.Replace(other, "mismatching-secret");
                row.StableProtectedCredential = other.StableProtectedCredential;
            }
            await corrupt.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext(failure == "save" ? [new FailAfterSave()] : []);
        var before = await db.PersonalLocationProviderProfiles.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        if (failure == "protection")
            codec = new LegacyCredentialPreparationCodec(provider, new StableDataProtectionProvider(new FailSecondProtection(provider)));
        var preparation = new StableIdentityPreparation(db, codec);
        Assert.Equal(1, await RunCliAsync("prepare-stable-identity", preparation));
        await using var verify = fixture.CreateContext();
        var after = await verify.PersonalLocationProviderProfiles.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        AssertOnlyCompanionsChanged(verify, before, after, successful: false);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(0, await verify.AuditLogs.CountAsync());
    }

    /// <summary>A real concurrent writer times out while preparation holds exclusion, then succeeds after commit.</summary>
    [PostgresFact]
    public async Task Preparation_ExcludesConcurrentProfileMutation()
    {
        await using var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        var provider = new EphemeralDataProtectionProvider();
        var owner = CredentialTestFactory.Create(provider);
        var codec = new LegacyCredentialPreparationCodec(provider, new StableDataProtectionProvider(provider));
        await SeedAsync(fixture, owner, provider);
        var gate = new PauseBeforeSave();
        await using var db = fixture.CreateContext(gate);
        var prepare = new StableIdentityPreparation(db, codec).PrepareAsync();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var writer = fixture.CreateContext();
            await writer.Database.OpenConnectionAsync();
            await writer.Database.ExecuteSqlRawAsync("SET lock_timeout = '200ms'");
            var exception = await Assert.ThrowsAsync<PostgresException>(() => writer.Database.ExecuteSqlRawAsync(
                """UPDATE "PersonalLocationProviderProfiles" SET "RoutingAuthorized" = NOT "RoutingAuthorized" """));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, exception.SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.True((await prepare).Ready);
        await using var after = fixture.CreateContext();
        Assert.Equal(4, await after.Database.ExecuteSqlRawAsync(
            """UPDATE "PersonalLocationProviderProfiles" SET "RoutingAuthorized" = NOT "RoutingAuthorized" """));
    }

    /// <summary>Legacy Mapbox plaintext is retired only after stable readback; failure preserves recovery.</summary>
    [PostgresFact]
    public async Task LegacyMapboxMigration_StableProtectsAndPreservesPlaintextOnStableFailure()
    {
        await using var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        var provider = new EphemeralDataProtectionProvider();
        var healthy = CredentialTestFactory.Create(provider);
        foreach (var fail in new[] { false, true })
        {
            var user = await fixture.CreateUserAsync();
            await using var db = fixture.CreateContext();
            db.ApiTokens.Add(new ApiToken
            {
                UserId = user.Id, User = await db.Users.SingleAsync(row => row.Id == user.Id),
                Name = "Mapbox", Token = StableIdentityCryptographyTests.Secret
            });
            await db.SaveChangesAsync();
            var owner = fail
                ? new PersonalProviderCredentialService(new StableIdentityCryptographyTests.ThrowingProvider())
                : healthy;
            var migration = new LegacyMapboxMigrationService(db, owner);
            if (fail)
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => migration.MigrateAsync(user.Id));
                Assert.False(exception.ToString().Contains(StableIdentityCryptographyTests.Secret, StringComparison.Ordinal));
            }
            else Assert.True((await migration.MigrateAsync(user.Id)).ProtectedCredentialReady);
            await using var verify = fixture.CreateContext();
            Assert.Equal(fail ? 1 : 0, await verify.ApiTokens.IgnoreQueryFilters().CountAsync(row => row.UserId == user.Id));
            var profile = await verify.PersonalLocationProviderProfiles.SingleOrDefaultAsync(row => row.UserId == user.Id);
            if (fail) Assert.Null(profile);
            else
            {
                Assert.NotNull(profile);
                Assert.True(healthy.Read(profile).Succeeded);
                Assert.Null(profile.ProtectedCredential);
            }
        }
    }


    /// <summary>Stable activation preserves API hashes and enforces persisted state, replacement and revocation with xmin.</summary>
    [PostgresFact]
    public async Task ActivationAndMutation_PreserveHashedApiTokenAndRejectStaleWriters()
    {
        await using var fixture = new PostgresMigrationTestFixture();
        await fixture.InitializeAsync();
        using var directory = new TestDirectory();
        await using var host = StableIdentityCryptographyTests.Host(directory.Path, Path.Combine(directory.Path, "ring"));
        var owner = host.Services.GetRequiredService<PersonalProviderCredentialService>();
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Mapbox);
        owner.Replace(profile, StableIdentityCryptographyTests.Secret);
        profile.ProtectedCredential = "retained-rollback-evidence";
        db.Add(profile);
        var hash = Wayfarer.Util.ApiTokenService.HashToken("ordinary-mobile-token");
        db.ApiTokens.Add(new ApiToken { UserId = user.Id, Name = "mobile", TokenHash = hash });
        await db.SaveChangesAsync();
        var readiness = new StableIdentityReadiness(db, owner);
        var tokens = new Wayfarer.Util.ApiTokenService(db, null!);
        Assert.True((await readiness.StatusAsync()).Ready);
        Assert.True(await tokens.ValidateApiTokenAsync(user.Id, "ordinary-mobile-token"));
        await using var staleDb = fixture.CreateContext();
        var stale = await staleDb.PersonalLocationProviderProfiles.SingleAsync();
        var generation = profile.CredentialGeneration;
        var version = profile.RowVersion;
        owner.Replace(profile, "new-credential");
        await db.SaveChangesAsync();
        Assert.Null(profile.ProtectedCredential);
        Assert.Equal(generation + 1, profile.CredentialGeneration);
        Assert.NotEqual(version, profile.RowVersion);
        Assert.True((await readiness.StatusAsync()).Ready);
        stale.RoutingAuthorized = true;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        profile.StableProtectedCredential = "unreadable";
        await db.SaveChangesAsync();
        Assert.Equal(1, (await readiness.StatusAsync()).Blocked);
        profile.StableProtectedCredential = null;
        profile.ProtectedCredential = "unprepared";
        await db.SaveChangesAsync();
        Assert.Equal(1, (await readiness.StatusAsync()).Pending);
        owner.Revoke(profile);
        await db.SaveChangesAsync();
        Assert.Equal(1, (await readiness.StatusAsync()).Inactive);
        profile.ProtectedCredential = "inconsistent";
        await db.SaveChangesAsync();
        Assert.Equal(1, (await readiness.StatusAsync()).Blocked);
        profile.ProtectedCredential = null;
        profile.RevokedAt = null;
        await db.SaveChangesAsync();
        Assert.Equal(1, (await readiness.StatusAsync()).Inactive);
        db.ChangeTracker.Clear();
        Assert.True(await tokens.ValidateApiTokenAsync(user.Id, "ordinary-mobile-token"));
        Assert.True((await db.ApiTokens.SingleAsync()).TokenHash == hash);
    }

    private static async Task SeedAsync(PostgresMigrationTestFixture fixture, PersonalProviderCredentialService owner, IDataProtectionProvider provider)
    {
        await using var db = fixture.CreateContext();
        for (var index = 0; index < 4; index++)
        {
            var user = await fixture.CreateUserAsync();
            var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Mapbox);
            if (index != 3)
            {
                owner.Replace(profile, StableIdentityCryptographyTests.Secret);
                profile.ProtectedCredential = PersonalProviderCredentialService.Protector(profile, provider).Protect(StableIdentityCryptographyTests.Secret);
                profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
                profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
                owner.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
                if (index != 0) profile.StableProtectedCredential = null;
            }
            db.Add(profile);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Checks every mapped value without dumping a secret-bearing row on assertion failure.</summary>
    private static void AssertOnlyCompanionsChanged(ApplicationDbContext db,
        List<PersonalLocationProviderProfile> before, List<PersonalLocationProviderProfile> after, bool successful)
    {
        Assert.Equal(before.Count, after.Count);
        for (var index = 0; index < before.Count; index++)
        {
            var pending = successful && before[index].ProtectedCredential != null && before[index].StableProtectedCredential == null;
            foreach (var property in db.Model.FindEntityType(typeof(PersonalLocationProviderProfile))!.GetProperties())
            {
                var oldValue = property.PropertyInfo!.GetValue(before[index]);
                var newValue = property.PropertyInfo.GetValue(after[index]);
                if (pending && property.Name is "StableProtectedCredential" or "RowVersion")
                    Assert.False(Equals(oldValue, newValue), "A prepared property must change.");
                else Assert.True(Equals(oldValue, newValue), "Preparation changed a preserved property.");
            }
        }
    }

    /// <summary>Captures command output and asserts only bounded counts reach diagnostics.</summary>
    private static async Task<int> RunCliAsync(string command, StableIdentityPreparation preparation)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await DataProtectionCli.ExecuteAsync(command, preparation, output, error);
        var text = output.ToString() + error;
        Assert.False(text.Contains(StableIdentityCryptographyTests.Secret, StringComparison.Ordinal));
        Assert.False(text.Contains("migration-fixture-", StringComparison.Ordinal));
        Assert.False(text.Contains("CfDJ", StringComparison.Ordinal));
        return code;
    }

    /// <summary>Fails after SQL was saved inside the outer migration transaction.</summary>
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(StableIdentityCryptographyTests.Secret);
    }

    /// <summary>Pauses inside preparation while its table lock is held.</summary>
    private sealed class PauseBeforeSave : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    /// <summary>Shares a failure counter through the real purpose chain and throws after one companion was computed.</summary>
    private sealed class FailSecondProtection(IDataProtectionProvider provider, int[]? calls = null) : IDataProtectionProvider, IDataProtector
    {
        private readonly int[] _calls = calls ?? [0];
        public IDataProtector CreateProtector(string purpose) => new FailSecondProtection(provider.CreateProtector(purpose), _calls);
        public byte[] Protect(byte[] plaintext) => ++_calls[0] == 2
            ? throw new CryptographicException(StableIdentityCryptographyTests.Secret) : ((IDataProtector)provider).Protect(plaintext);
        public byte[] Unprotect(byte[] protectedData) => ((IDataProtector)provider).Unprotect(protectedData);
    }
}
