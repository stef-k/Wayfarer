using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Runs one explicit bounded and resumable Geoapify Location enrichment invocation.</summary>
public sealed class GeoapifyLocationBackfillService(
    ApplicationDbContext dbContext, ReverseGeocodingService reverseGeocoding,
    IDbContextFactory<ApplicationDbContext> dbContextFactory) : ILocationEnrichmentBatch
{
    /// <summary>Gets the strict maximum records scanned by one invocation.</summary>
    public const int MaximumRecords = 100;

    /// <summary>Runs one user-owned chronological invocation and returns content-free progress.</summary>
    public async Task<GeoapifyBackfillResult> RunAsync(string userId, int epoch,
        CancellationToken cancellationToken = default)
        => await RunSerializedAsync(userId, epoch, cancellationToken);

    /// <summary>Retains the explicit bounded #502 action without requiring workflow state.</summary>
    public async Task<GeoapifyBackfillResult> RunAsync(
        string userId, CancellationToken cancellationToken = default)
        => await RunSerializedAsync(userId, null, cancellationToken);

    private async Task<GeoapifyBackfillResult> RunSerializedAsync(string userId, int? epoch,
        CancellationToken cancellationToken)
    {
        await using var lockOwner = dbContext.Database.IsNpgsql()
            ? await dbContextFactory.CreateDbContextAsync(cancellationToken) : null;
        await using var lockTransaction = lockOwner == null
            ? null : await lockOwner.Database.BeginTransactionAsync(cancellationToken);
        if (lockOwner != null)
            _ = await lockOwner.Users.FromSqlInterpolated($$"""
                SELECT * FROM "AspNetUsers" WHERE "Id" = {{userId}} FOR UPDATE
                """).AsNoTracking().SingleAsync(cancellationToken);

        try
        {
            return await RunOperationalAsync(userId, epoch, cancellationToken);
        }
        finally
        {
            // This transaction owns only invocation serialization. Operational transactions commit independently.
            if (lockTransaction != null) await lockTransaction.RollbackAsync(CancellationToken.None);
        }
    }

    private async Task<GeoapifyBackfillResult> RunOperationalAsync(
        string userId, int? epoch, CancellationToken cancellationToken)
    {
        var authority = await LoadAuthorityAsync(userId, cancellationToken);
        var ids = await LoadCandidateIdsAsync(dbContext, userId, authority, MaximumRecords, cancellationToken);
        var scanned = 0; var succeeded = 0; var noResult = 0; var unavailable = 0; var exhausted = false;
        DateTimeOffset? nextEligibleAt = null;
        var authorityUnavailable = false;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (epoch.HasValue && !await ExecutionStillCurrentAsync(userId, epoch.Value, cancellationToken)) break;
            var location = await dbContext.Locations.SingleAsync(
                item => item.Id == id && item.UserId == userId, cancellationToken);
            if (!IsWhollyUnenriched(location)) continue;
            scanned++;
            var result = await reverseGeocoding.EnrichAsync(userId,
                location.Coordinates.Y, location.Coordinates.X,
                ReverseGeocodingIntent.ImportMissingAddress, cancellationToken);
            if (!epoch.HasValue && result.Category == ReverseGeocodingCategory.CancelledAfterContact)
                cancellationToken.ThrowIfCancellationRequested();
            if (epoch.HasValue && !await ExecutionStillCurrentAsync(userId, epoch.Value, CancellationToken.None))
            {
                if (result.Authority != null)
                    await RecordAttemptAsync(userId, location.Id, authority,
                        ReverseGeocodingResult.Unavailable(ReverseGeocodingCategory.CancelledAfterContact)
                            with { Authority = result.Authority }, CancellationToken.None);
                break;
            }
            if (result.Category == ReverseGeocodingCategory.Exhausted)
            {
                exhausted = true;
                nextEligibleAt = await LoadBudgetWakeAsync(userId, cancellationToken);
                break;
            }
            if (result.Category is ReverseGeocodingCategory.Unauthorized or ReverseGeocodingCategory.CredentialRequired
                or ReverseGeocodingCategory.NoProviderSelected or ReverseGeocodingCategory.VerificationRequired
                or ReverseGeocodingCategory.ConsentRequired or ReverseGeocodingCategory.StaleAuthority)
            { authorityUnavailable = true; break; }
            if (!result.Succeeded)
            {
                if (epoch.HasValue)
                    await RecordAttemptAsync(userId, location.Id, authority, result, cancellationToken);
                if (result.Category is ReverseGeocodingCategory.InvalidResponse or ReverseGeocodingCategory.InvalidRequest)
                    noResult++;
                else unavailable++;
                if (result.Category == ReverseGeocodingCategory.Authorization)
                { authorityUnavailable = true; break; }
                continue;
            }
            await dbContext.Entry(location).ReloadAsync(cancellationToken);
            if (!IsWhollyUnenriched(location)) continue;
            if (result.ApplyTo(location, DateTimeOffset.UtcNow))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await dbContext.LocationEnrichmentAttempts.Where(item => item.UserId == userId && item.LocationId == id)
                    .ExecuteDeleteAsync(CancellationToken.None);
                succeeded++;
            }
        }
        var remaining = await WhollyUnenriched(dbContext.Locations.Where(item => item.UserId == userId))
            .CountAsync(cancellationToken);
        return new(scanned, succeeded, noResult, unavailable, remaining, exhausted, nextEligibleAt, authorityUnavailable);
    }

    /// <summary>Loads only stable candidate identities in chronological order.</summary>
    public static Task<List<int>> LoadCandidateIdsAsync(ApplicationDbContext dbContext, string userId, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumRecords) throw new ArgumentOutOfRangeException(nameof(limit));
        return WhollyUnenriched(dbContext.Locations.Where(item => item.UserId == userId))
            .OrderBy(item => item.Timestamp).ThenBy(item => item.Id).Select(item => item.Id).Take(limit)
            .ToListAsync(cancellationToken);
    }

    private Task<bool> ExecutionStillCurrentAsync(string userId, int epoch, CancellationToken cancellationToken) =>
        dbContext.LocationEnrichmentWorkflows.AsNoTracking().AnyAsync(item => item.UserId == userId
            && item.State == LocationEnrichmentState.Running && item.IntentEnabled && item.Epoch == epoch,
            cancellationToken);

    /// <summary>Loads due identities while permanently deferred poison rows cannot consume the batch.</summary>
    public static Task<List<int>> LoadCandidateIdsAsync(ApplicationDbContext dbContext, string userId,
        EnrichmentAuthority authority, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumRecords) throw new ArgumentOutOfRangeException(nameof(limit));
        var now = DateTime.UtcNow;
        var attempts = dbContext.LocationEnrichmentAttempts.Where(item => item.UserId == userId);
        return (from location in WhollyUnenriched(dbContext.Locations.Where(item => item.UserId == userId))
                join attempt in attempts on location.Id equals attempt.LocationId into matches
                from attempt in matches.DefaultIfEmpty()
                where attempt == null ||
                    (attempt.Outcome != LocationEnrichmentOutcome.InvalidCoordinates
                     && attempt.Outcome != LocationEnrichmentOutcome.NoResult
                     && attempt.Outcome != LocationEnrichmentOutcome.AttemptLimit
                     && (attempt.ProviderKey != authority.ProviderKey
                         || attempt.CredentialGeneration != authority.CredentialGeneration
                         || attempt.ConfigurationGeneration != authority.ConfigurationGeneration
                         || attempt.SelectionGeneration != authority.SelectionGeneration
                         || (attempt.AdmittedAttemptCount < 3
                             && (attempt.NextAttemptAtUtc == null || attempt.NextAttemptAtUtc <= now))))
                orderby location.Timestamp, location.Id
                select location.Id).Take(limit).ToListAsync(cancellationToken);
    }

    private async Task<EnrichmentAuthority> LoadAuthorityAsync(string userId, CancellationToken cancellationToken)
    {
        var selection = await dbContext.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var key = selection?.GeocodingProviderKey ?? string.Empty;
        var profile = await dbContext.PersonalLocationProviderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == key, cancellationToken);
        return new(key, profile?.CredentialGeneration ?? 0, profile?.GeocodingGeneration ?? 0,
            selection?.GeocodingSelectionGeneration ?? 0);
    }

    private async Task RecordAttemptAsync(string userId, int locationId, EnrichmentAuthority selected,
        ReverseGeocodingResult result, CancellationToken cancellationToken)
    {
        var contacted = result.Authority;
        var authority = contacted == null ? selected : new EnrichmentAuthority(contacted.ProviderKey,
            contacted.CredentialGeneration, contacted.CapabilityGeneration, contacted.SelectionGeneration);
        var attempt = await dbContext.LocationEnrichmentAttempts.SingleOrDefaultAsync(
            item => item.UserId == userId && item.LocationId == locationId,
            cancellationToken);
        // The workflow user is the only caller identity; no request payload is retained.
        attempt ??= new LocationEnrichmentAttempt { UserId = userId, LocationId = locationId };
        if (attempt.Id == 0) dbContext.Add(attempt);
        var sameGeneration = attempt.ProviderKey == authority.ProviderKey
            && attempt.CredentialGeneration == authority.CredentialGeneration
            && attempt.ConfigurationGeneration == authority.ConfigurationGeneration
            && attempt.SelectionGeneration == authority.SelectionGeneration;
        if (!sameGeneration) attempt.AdmittedAttemptCount = 0;
        attempt.ProviderKey = authority.ProviderKey;
        attempt.CredentialGeneration = authority.CredentialGeneration;
        attempt.ConfigurationGeneration = authority.ConfigurationGeneration;
        attempt.SelectionGeneration = authority.SelectionGeneration;
        if (contacted != null) attempt.AdmittedAttemptCount++;
        attempt.LastAttemptAtUtc = DateTime.UtcNow;
        attempt.Outcome = result.Category switch
        {
            ReverseGeocodingCategory.InvalidRequest => LocationEnrichmentOutcome.InvalidCoordinates,
            ReverseGeocodingCategory.InvalidResponse => LocationEnrichmentOutcome.NoResult,
            ReverseGeocodingCategory.Authorization or ReverseGeocodingCategory.StaleAuthority
                => LocationEnrichmentOutcome.AuthorityUnavailable,
            _ when attempt.AdmittedAttemptCount >= 3 => LocationEnrichmentOutcome.AttemptLimit,
            _ => LocationEnrichmentOutcome.RetryableFailure
        };
        attempt.NextAttemptAtUtc = attempt.Outcome == LocationEnrichmentOutcome.RetryableFailure
            ? DateTime.UtcNow + LocationEnrichmentRetryPolicy.Backoff(attempt.AdmittedAttemptCount) : null;
        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<DateTimeOffset?> LoadBudgetWakeAsync(string userId, CancellationToken cancellationToken)
    {
        var now = dbContext.Database.IsNpgsql()
            ? await dbContext.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync(cancellationToken)
            : DateTimeOffset.UtcNow;
        var selection = await dbContext.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (selection?.GeocodingProviderKey == "mapbox") return LocationEnrichmentRetryPolicy.MapboxWake(now);
        var admissions = await dbContext.GeoapifyUsageAdmissions.AsNoTracking()
            .Where(item => item.UserId == userId && item.AdmittedAt > now.AddHours(-24))
            .Select(item => item.AdmittedAt).ToListAsync(cancellationToken);
        return LocationEnrichmentRetryPolicy.TryGeoapifyWake(now, admissions);
    }

    /// <summary>Returns whether every enrichment and provenance field is empty.</summary>
    public static bool IsWhollyUnenriched(Location value) => string.IsNullOrWhiteSpace(value.Address)
        && string.IsNullOrWhiteSpace(value.FullAddress) && string.IsNullOrWhiteSpace(value.AddressNumber)
        && string.IsNullOrWhiteSpace(value.StreetName) && string.IsNullOrWhiteSpace(value.PostCode)
        && string.IsNullOrWhiteSpace(value.Place) && string.IsNullOrWhiteSpace(value.Region)
        && string.IsNullOrWhiteSpace(value.Country) && value.ReverseGeocodingProvider == null
        && value.ReverseGeocodingStorageMode == null && value.ReverseGeocodedAt == null;

    private static IQueryable<Location> WhollyUnenriched(IQueryable<Location> query) => query.Where(value =>
        (value.Address == null || value.Address == "") && (value.FullAddress == null || value.FullAddress == "")
        && (value.AddressNumber == null || value.AddressNumber == "") && (value.StreetName == null || value.StreetName == "")
        && (value.PostCode == null || value.PostCode == "") && (value.Place == null || value.Place == "")
        && (value.Region == null || value.Region == "") && (value.Country == null || value.Country == "")
        && value.ReverseGeocodingProvider == null && value.ReverseGeocodingStorageMode == null
        && value.ReverseGeocodedAt == null);
}

/// <summary>Creates independent contexts for transaction-scoped backfill lock ownership.</summary>
public sealed class BackfillLockDbContextFactory(
    DbContextOptions<ApplicationDbContext> options, IServiceProvider services)
    : IDbContextFactory<ApplicationDbContext>
{
    /// <summary>Creates a context whose connection is never shared with operational persistence.</summary>
    public ApplicationDbContext CreateDbContext() => new(options, services);
}

/// <summary>Contains bounded content-free progress for one explicit backfill invocation.</summary>
public sealed record GeoapifyBackfillResult(
    int Scanned, int Succeeded, int NoResult, int Unavailable, int RemainingEstimate, bool Exhausted,
    DateTimeOffset? NextEligibleAt = null, bool AuthorityUnavailable = false);

/// <summary>Contains only bounded provider generation identity used by candidate selection.</summary>
public sealed record EnrichmentAuthority(string ProviderKey, int CredentialGeneration,
    int ConfigurationGeneration, int SelectionGeneration);
