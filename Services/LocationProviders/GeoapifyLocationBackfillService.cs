using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Runs one explicit bounded and resumable Geoapify Location enrichment invocation.</summary>
public sealed class GeoapifyLocationBackfillService(
    ApplicationDbContext dbContext, ReverseGeocodingService reverseGeocoding)
{
    /// <summary>Gets the strict maximum records scanned by one invocation.</summary>
    public const int MaximumRecords = 100;

    /// <summary>Runs one user-owned chronological invocation and returns content-free progress.</summary>
    public async Task<GeoapifyBackfillResult> RunAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = dbContext.Database.IsNpgsql()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        if (transaction != null)
        {
            // The exact user row is the durable invocation authority. Holding it across bounded provider calls is
            // intentional: candidate selection cannot otherwise guarantee at-most-one admission/contact per Location.
            _ = await dbContext.Users.FromSqlInterpolated($$"""
                SELECT * FROM "AspNetUsers" WHERE "Id" = {{userId}} FOR UPDATE
                """).AsNoTracking().SingleAsync(cancellationToken);
        }
        var ids = await LoadCandidateIdsAsync(dbContext, userId, MaximumRecords, cancellationToken);
        var scanned = 0; var succeeded = 0; var noResult = 0; var unavailable = 0; var exhausted = false;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = await dbContext.Locations.SingleAsync(
                item => item.Id == id && item.UserId == userId, cancellationToken);
            if (!IsWhollyUnenriched(location)) continue;
            scanned++;
            var result = await reverseGeocoding.EnrichAsync(userId,
                location.Coordinates.Y, location.Coordinates.X,
                ReverseGeocodingIntent.ImportMissingAddress, cancellationToken);
            if (result.Category == ReverseGeocodingCategory.Exhausted) { exhausted = true; break; }
            if (result.Category is ReverseGeocodingCategory.Unauthorized or ReverseGeocodingCategory.CredentialRequired
                or ReverseGeocodingCategory.NoProviderSelected or ReverseGeocodingCategory.VerificationRequired
                or ReverseGeocodingCategory.StaleAuthority) break;
            if (!result.Succeeded)
            {
                if (result.Category == ReverseGeocodingCategory.InvalidResponse) noResult++; else unavailable++;
                continue;
            }
            await dbContext.Entry(location).ReloadAsync(cancellationToken);
            if (!IsWhollyUnenriched(location)) continue;
            if (result.ApplyTo(location, DateTimeOffset.UtcNow))
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                succeeded++;
            }
        }
        var remaining = await WhollyUnenriched(dbContext.Locations.Where(item => item.UserId == userId))
            .CountAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return new(scanned, succeeded, noResult, unavailable, remaining, exhausted);
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

/// <summary>Contains bounded content-free progress for one explicit backfill invocation.</summary>
public sealed record GeoapifyBackfillResult(
    int Scanned, int Succeeded, int NoResult, int Unavailable, int RemainingEstimate, bool Exhausted);
