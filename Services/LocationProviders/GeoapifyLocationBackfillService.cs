using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Runs a bounded batch under one workflow lease and one durable operation per admitted contact.</summary>
public sealed class GeoapifyLocationBackfillService(
    IDbContextFactory<ApplicationDbContext> contexts, IServiceScopeFactory scopes,
    IHttpClientFactory clients, ILogger<BaseApiController> logger,
    LocationEnrichmentExecutionAuthority executionAuthority) : ILocationEnrichmentBatch
{
    public const int MaximumRecords = 100;

    /// <summary>Routes the retained explicit action through the same durable workflow lease.</summary>
    public async Task<GeoapifyBackfillResult> RunAsync(string userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var workflow = await db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(
            item => item.UserId == userId, cancellationToken);
        if (workflow is null)
        {
            var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
            workflow = LocationEnrichmentWorkflow.Create(userId, now);
            workflow.Start(now);
            db.Add(workflow);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!workflow.IntentEnabled)
        {
            var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
            workflow.Start(now);
            await db.SaveChangesAsync(cancellationToken);
        }
        return await RunAsync(userId, workflow.Epoch, cancellationToken);
    }

    /// <summary>Acquires the common lease before entering the bounded implementation.</summary>
    public async Task<GeoapifyBackfillResult> RunAsync(string userId, int epoch,
        CancellationToken cancellationToken = default)
    {
        var owner = await executionAuthority.TryAcquireAsync(userId, epoch, cancellationToken);
        if (!owner.HasValue) return new(0, 0, 0, 0, 0, false);
        try { return await RunAsync(owner.Value, cancellationToken); }
        finally { await executionAuthority.TryReleaseAsync(owner.Value, CancellationToken.None); }
    }

    public async Task<GeoapifyBackfillResult> RunAsync(LocationEnrichmentExecutionLease owner,
        CancellationToken cancellationToken = default)
    {
        var selected = await LoadAuthorityAsync(owner.UserId, cancellationToken);
        var ids = await LoadCandidateIdsAsync(owner.UserId, selected, MaximumRecords, cancellationToken);
        var scanned = 0; var succeeded = 0; var skipped = 0; var noResult = 0; var unavailable = 0; var admitted = 0;
        var exhausted = false; var authorityUnavailable = false; DateTimeOffset? next = null;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var renewed = await executionAuthority.TryRenewForContactAsync(owner, cancellationToken);
            if (!renewed.HasValue) break;
            owner = renewed.Value;
            var candidate = await LoadCandidateAsync(owner.UserId, id, cancellationToken);
            if (candidate is null) { skipped++; continue; }
            scanned++;

            PersonalProviderAdmission admission;
            await using (var scope = scopes.CreateAsyncScope())
                admission = await scope.ServiceProvider.GetRequiredService<PersonalProviderContactGate>()
                    .AdmitPersistentGeocodingAsync(owner.UserId, cancellationToken);
            if (!admission.Succeeded)
            {
                var category = MapAdmission(admission.Category);
                exhausted = category == ReverseGeocodingCategory.Exhausted;
                authorityUnavailable = category is ReverseGeocodingCategory.Unauthorized
                    or ReverseGeocodingCategory.CredentialRequired or ReverseGeocodingCategory.NoProviderSelected
                    or ReverseGeocodingCategory.VerificationRequired or ReverseGeocodingCategory.ConsentRequired;
                if (exhausted) next = await LoadBudgetWakeAsync(owner.UserId, cancellationToken);
                break;
            }

            var operation = await TryClaimAttemptAsync(owner, id, admission.Authority!, cancellationToken);
            if (!operation.HasValue) continue;
            admitted++;
            if (!await executionAuthority.IsCurrentAsync(owner, CancellationToken.None)) break;
            var transport = new ReverseGeocodingService(
                clients.CreateClient("LocationEnrichmentProvider"), logger);
            var result = await transport.ContactAdmittedAsync(admission.Authority!, candidate.Value.Latitude,
                candidate.Value.Longitude, cancellationToken);
            var applied = await TryCompleteAttemptAsync(owner, id, operation.Value, result, CancellationToken.None);
            if (!applied.AuthorityCurrent) break;
            if (applied.Enriched) succeeded++;
            else if (result.Category is ReverseGeocodingCategory.InvalidRequest or ReverseGeocodingCategory.InvalidResponse)
                noResult++;
            else unavailable++;
            if (result.Category is ReverseGeocodingCategory.Authorization or ReverseGeocodingCategory.StaleAuthority)
            { authorityUnavailable = true; break; }
        }

        await using var finalDb = await contexts.CreateDbContextAsync(CancellationToken.None);
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(finalDb, CancellationToken.None);
        var remaining = await CandidateQuery(finalDb, owner.UserId, selected, now).CountAsync(CancellationToken.None);
        if (remaining == 0 && !exhausted && !authorityUnavailable)
        {
            var due = await FutureRetryQuery(finalDb, owner.UserId, selected, now)
                .MinAsync(item => (DateTime?)item.NextAttemptAtUtc, CancellationToken.None);
            if (due.HasValue) { remaining = 1; unavailable = Math.Max(unavailable, 1); next = due; }
        }
        return new(scanned, succeeded, noResult, unavailable, remaining, exhausted, next,
            authorityUnavailable, admitted, skipped);
    }

    private async Task<(double Latitude, double Longitude)?> LoadCandidateAsync(
        string userId, int id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == id, cancellationToken);
        return location is not null && IsWhollyUnenriched(location)
            ? (location.Coordinates.Y, location.Coordinates.X) : null;
    }

    private async Task<Guid?> TryClaimAttemptAsync(LocationEnrichmentExecutionLease owner, int locationId,
        PersonalProviderAuthoritySnapshot contacted, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        var workflow = await LockWorkflowAsync(db, owner.UserId, cancellationToken);
        var location = await db.Locations.SingleOrDefaultAsync(item => item.UserId == owner.UserId
            && item.Id == locationId, cancellationToken);
        if (workflow?.Epoch != owner.Epoch || !workflow.HasExecutionLease(owner.LeaseId,
                owner.FencingGeneration, now) || location is null || !IsWhollyUnenriched(location))
        { if (transaction != null) await transaction.RollbackAsync(cancellationToken); return null; }
        var attempt = await db.LocationEnrichmentAttempts.SingleOrDefaultAsync(item => item.UserId == owner.UserId
            && item.LocationId == locationId, cancellationToken) ?? new LocationEnrichmentAttempt
            { UserId = owner.UserId, LocationId = locationId };
        if (attempt.Id == 0) db.Add(attempt);
        var same = attempt.ProviderKey == contacted.ProviderKey
            && attempt.CredentialGeneration == contacted.CredentialGeneration
            && attempt.ConfigurationGeneration == contacted.CapabilityGeneration
            && attempt.SelectionGeneration == contacted.SelectionGeneration;
        if (!same) attempt.AdmittedAttemptCount = 0;
        attempt.ProviderKey = contacted.ProviderKey;
        attempt.CredentialGeneration = contacted.CredentialGeneration;
        attempt.ConfigurationGeneration = contacted.CapabilityGeneration;
        attempt.SelectionGeneration = contacted.SelectionGeneration;
        attempt.AdmittedAttemptCount++;
        attempt.Outcome = LocationEnrichmentOutcome.RetryableFailure;
        attempt.LastAttemptAtUtc = now;
        attempt.NextAttemptAtUtc = now + LocationEnrichmentRetryPolicy.Backoff(attempt.AdmittedAttemptCount);
        attempt.OperationId = Guid.NewGuid();
        attempt.OperationFencingGeneration = owner.FencingGeneration;
        attempt.OperationStartedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return attempt.OperationId;
    }

    private async Task<(bool AuthorityCurrent, bool Enriched)> TryCompleteAttemptAsync(
        LocationEnrichmentExecutionLease owner, int locationId, Guid operationId,
        ReverseGeocodingResult result, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        var workflow = await LockWorkflowAsync(db, owner.UserId, cancellationToken);
        var attempt = await db.LocationEnrichmentAttempts.SingleOrDefaultAsync(item => item.UserId == owner.UserId
            && item.LocationId == locationId && item.OperationId == operationId
            && item.OperationFencingGeneration == owner.FencingGeneration, cancellationToken);
        if (workflow?.Epoch != owner.Epoch || !workflow.HasExecutionLease(owner.LeaseId,
                owner.FencingGeneration, now) || attempt is null)
        { if (transaction != null) await transaction.RollbackAsync(cancellationToken); return (false, false); }
        attempt.Outcome = MapOutcome(result.Category, attempt.AdmittedAttemptCount);
        if (attempt.Outcome != LocationEnrichmentOutcome.RetryableFailure) attempt.NextAttemptAtUtc = null;
        attempt.OperationId = null; attempt.OperationFencingGeneration = null; attempt.OperationStartedAtUtc = null;
        var enriched = false;
        if (result.Succeeded)
        {
            var location = await db.Locations.SingleAsync(item => item.UserId == owner.UserId
                && item.Id == locationId, cancellationToken);
            enriched = IsWhollyUnenriched(location) && result.ApplyTo(location, new(now, TimeSpan.Zero));
            if (enriched) db.LocationEnrichmentAttempts.Remove(attempt);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return (true, enriched);
    }

    private async Task<List<int>> LoadCandidateIdsAsync(string userId, EnrichmentAuthority authority,
        int limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        return await CandidateQuery(db, userId, authority, now).Take(limit).ToListAsync(cancellationToken);
    }

    public static Task<List<int>> LoadCandidateIdsAsync(ApplicationDbContext db, string userId, int limit,
        CancellationToken cancellationToken = default) => WhollyUnenriched(db.Locations.Where(item => item.UserId == userId))
        .OrderBy(item => item.Timestamp).ThenBy(item => item.Id).Select(item => item.Id).Take(limit).ToListAsync(cancellationToken);

    public static Task<List<int>> LoadCandidateIdsAsync(ApplicationDbContext db, string userId,
        EnrichmentAuthority authority, int limit, CancellationToken cancellationToken = default)
        => CandidateQuery(db, userId, authority, DateTime.UtcNow).Take(limit).ToListAsync(cancellationToken);

    internal static IQueryable<int> CandidateQuery(ApplicationDbContext db, string userId,
        EnrichmentAuthority authority, DateTime now)
    {
        var attempts = db.LocationEnrichmentAttempts.Where(item => item.UserId == userId);
        return from location in WhollyUnenriched(db.Locations.Where(item => item.UserId == userId))
            join attempt in attempts on location.Id equals attempt.LocationId into matches
            from attempt in matches.DefaultIfEmpty()
            where attempt == null || (attempt.OperationId == null
                && attempt.Outcome != LocationEnrichmentOutcome.InvalidCoordinates
                && attempt.Outcome != LocationEnrichmentOutcome.NoResult
                && attempt.Outcome != LocationEnrichmentOutcome.AttemptLimit
                && attempt.ProviderKey == authority.ProviderKey
                && attempt.CredentialGeneration == authority.CredentialGeneration
                && attempt.ConfigurationGeneration == authority.ConfigurationGeneration
                && attempt.SelectionGeneration == authority.SelectionGeneration
                && attempt.AdmittedAttemptCount < 3
                && (attempt.NextAttemptAtUtc == null || attempt.NextAttemptAtUtc <= now))
            orderby location.Timestamp, location.Id select location.Id;
    }

    private static IQueryable<LocationEnrichmentAttempt> FutureRetryQuery(ApplicationDbContext db,
        string userId, EnrichmentAuthority authority, DateTime now) =>
        from attempt in db.LocationEnrichmentAttempts
        join location in WhollyUnenriched(db.Locations.Where(item => item.UserId == userId))
            on attempt.LocationId equals location.Id
        where attempt.UserId == userId && attempt.ProviderKey == authority.ProviderKey
            && attempt.CredentialGeneration == authority.CredentialGeneration
            && attempt.ConfigurationGeneration == authority.ConfigurationGeneration
            && attempt.SelectionGeneration == authority.SelectionGeneration
            && attempt.Outcome == LocationEnrichmentOutcome.RetryableFailure
            && attempt.AdmittedAttemptCount < 3 && attempt.NextAttemptAtUtc > now select attempt;

    private async Task<EnrichmentAuthority> LoadAuthorityAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var selection = await db.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var key = selection?.GeocodingProviderKey ?? string.Empty;
        var profile = await db.PersonalLocationProviderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == key, cancellationToken);
        return new(key, profile?.CredentialGeneration ?? 0, profile?.GeocodingGeneration ?? 0,
            selection?.GeocodingSelectionGeneration ?? 0);
    }

    private async Task<DateTimeOffset?> LoadBudgetWakeAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        var selection = await db.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (selection?.GeocodingProviderKey == "mapbox")
            return LocationEnrichmentRetryPolicy.MapboxWake(new(now, TimeSpan.Zero));
        var admissions = await db.GeoapifyUsageAdmissions.AsNoTracking().Where(item => item.UserId == userId
            && item.AdmittedAt > now.AddHours(-24)).Select(item => item.AdmittedAt).ToListAsync(cancellationToken);
        return LocationEnrichmentRetryPolicy.TryGeoapifyWake(new(now, TimeSpan.Zero), admissions);
    }

    private static Task<LocationEnrichmentWorkflow?> LockWorkflowAsync(ApplicationDbContext db, string userId,
        CancellationToken cancellationToken) => db.Database.IsNpgsql()
        ? db.LocationEnrichmentWorkflows.FromSqlInterpolated($$"""
            SELECT *, xmin FROM "LocationEnrichmentWorkflows" WHERE "UserId" = {{userId}} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);

    private static LocationEnrichmentOutcome MapOutcome(ReverseGeocodingCategory category, int count) => category switch
    {
        ReverseGeocodingCategory.Success => LocationEnrichmentOutcome.None,
        ReverseGeocodingCategory.InvalidRequest => LocationEnrichmentOutcome.InvalidCoordinates,
        ReverseGeocodingCategory.InvalidResponse => LocationEnrichmentOutcome.NoResult,
        ReverseGeocodingCategory.Authorization or ReverseGeocodingCategory.StaleAuthority
            => LocationEnrichmentOutcome.AuthorityUnavailable,
        _ when count >= 3 => LocationEnrichmentOutcome.AttemptLimit,
        _ => LocationEnrichmentOutcome.RetryableFailure
    };

    private static ReverseGeocodingCategory MapAdmission(PersonalProviderAdmissionCategory category) => category switch
    {
        PersonalProviderAdmissionCategory.NoProviderSelected => ReverseGeocodingCategory.NoProviderSelected,
        PersonalProviderAdmissionCategory.ConsentRequired => ReverseGeocodingCategory.ConsentRequired,
        PersonalProviderAdmissionCategory.Unauthorized => ReverseGeocodingCategory.Unauthorized,
        PersonalProviderAdmissionCategory.Unverified => ReverseGeocodingCategory.VerificationRequired,
        PersonalProviderAdmissionCategory.Exhausted => ReverseGeocodingCategory.Exhausted,
        PersonalProviderAdmissionCategory.CredentialUnavailable => ReverseGeocodingCategory.CredentialRequired,
        _ => ReverseGeocodingCategory.ProviderUnavailable
    };

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

public sealed record GeoapifyBackfillResult(int Scanned, int Succeeded, int NoResult, int Unavailable,
    int RemainingEstimate, bool Exhausted, DateTimeOffset? NextEligibleAt = null,
    bool AuthorityUnavailable = false, int Admitted = 0, int Skipped = 0, int FailedBatches = 0);

public sealed record EnrichmentAuthority(string ProviderKey, int CredentialGeneration,
    int ConfigurationGeneration, int SelectionGeneration);
