using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using System.Security.Cryptography;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

public partial class TripImportService
{
    /// <summary>Validates the exact file before showing current-content-bound owner replacement choices.</summary>
    public async Task<NativeImportReview> ReviewNativeAsync(Stream file, string owner, CancellationToken cancellationToken = default)
    {
        var (classification, digest) = await ReadInputAsync(file, cancellationToken);
        var source = classification.Document ?? throw new TripImportValidationException("Only native KML supports update.");
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            // Uploaded IDs cannot acquire foreign graph barriers; admission is rechecked after waits below.
            if (!await _dbContext.Trips.AsNoTracking().AnyAsync(t => t.Id == source.TripId && t.UserId == owner, cancellationToken))
                throw new InvalidOperationException("Trip not found or not yours for upsert.");
            var profiles = await _dbContext.Set<TransportProfile>().AsNoTracking().ToDictionaryAsync(p => p.Key, cancellationToken);
            var incoming = WayfarerKmlAggregateMapper.Map(source, owner, profiles, false);
            TripRichNotes.NormalizeDestination(incoming);
            var current = await LockReplacementAsync(source.TripId, owner, cancellationToken);
            await ValidateReplacementIdentitiesAsync(incoming, cancellationToken);
            PreservePortableOmissions(source, current, incoming);
            TripImportTagReconciler.ValidateTokens(source.Tags);
            return _review.Preview(current, incoming, NativeImportReviewProtection.Binding(current, owner, digest));
        }
        finally { _dbContext.ChangeTracker.Clear(); }
    }

    /// <summary>Reviewed updates cannot fall back to generic input, Auto, or a different target.</summary>
    public async Task<TripImportResult> ImportReviewedNativeAsync(Stream file, string owner, NativeImportApproval approval,
        CancellationToken cancellationToken = default)
    {
        var (classification, digest) = await ReadInputAsync(file, cancellationToken);
        var source = classification.Document ?? throw new TripImportValidationException("Only native KML supports update.");
        return new(await ImportNativeAsync(source, owner, TripImportMode.Upsert, digest, approval, cancellationToken), []);
    }

    /// <summary>Older envelopes omit portable intent; retain root and same-type Segment fields across reconstruction.</summary>
    private static void PreservePortableOmissions(WayfarerKmlDocument source, Trip current, Trip incoming)
    {
        if (source.Version >= 5) return;
        incoming.Credits = current.Credits;
        incoming.ContributionPolicy = current.ContributionPolicy;
        var titles = current.Segments.ToDictionary(s => s.Id, s => s.Title);
        foreach (var segment in incoming.Segments)
            if (titles.TryGetValue(segment.Id, out var title)) segment.Title = title;
    }

    /// <summary>Hashes every original byte, including encoding and trailing whitespace, inside the established budget.</summary>
    private static async Task<(WayfarerKmlClassification Classification, string Digest)> ReadInputAsync(Stream input, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + count > WayfarerKmlParser.MaximumEncodedDocumentBytes)
                throw new TripImportValidationException("The file exceeds the supported limit.");
            bytes.Write(buffer, 0, count);
        }
        var digest = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
        bytes.Position = 0;
        return (await WayfarerKmlParser.ClassifyAndParseAsync(bytes, token), digest);
    }

    /// <summary>Uploaded identities may retain only same-type objects already belonging to this owned Trip.</summary>
    private async Task ValidateReplacementIdentitiesAsync(Trip incoming, CancellationToken token)
    {
        var targets = NativeTripGraph.Targets(incoming).ToDictionary(t => t.Id, t => t.Kind);
        var ids = targets.Keys.ToArray();
        var stored = await _dbContext.Trips.Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, TripId = t.Id, Kind = "trip" }).ToListAsync(token);
        stored.AddRange(await _dbContext.Regions.Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.TripId, Kind = "region" }).ToListAsync(token));
        stored.AddRange(await _dbContext.Places.Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Region.TripId, Kind = "place" }).ToListAsync(token));
        stored.AddRange(await _dbContext.Areas.Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Region.TripId, Kind = "area" }).ToListAsync(token));
        stored.AddRange(await _dbContext.Segments.Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.TripId, Kind = "segment" }).ToListAsync(token));
        if (stored.Any(item => item.TripId != incoming.Id || item.Kind != targets[item.Id]))
            throw new TripImportValidationException("The imported identities cannot be applied to this Trip.");
    }

    /// <summary>Fences current writers, then reads complete current owner originals after all lock waits.</summary>
    private async Task<Trip> LockReplacementAsync(Guid tripId, string owner, CancellationToken token)
    {
        if (_dbContext.Database.IsRelational())
        {
            // Match lifecycle lock classes. Parent barriers fence additions; re-read all content after waits.
            await _dbContext.Segments.FromSqlInterpolated($"SELECT *, xmin FROM public.\"Segments\" WHERE \"TripId\" = {tripId} ORDER BY \"Id\" FOR UPDATE").AsNoTracking().ToListAsync(token);
            await _dbContext.Places.FromSqlInterpolated($"SELECT p.* FROM public.\"Places\" p JOIN public.\"Regions\" r ON p.\"RegionId\" = r.\"Id\" WHERE r.\"TripId\" = {tripId} ORDER BY p.\"Id\" FOR UPDATE OF p").AsNoTracking().ToListAsync(token);
            await _dbContext.Areas.FromSqlInterpolated($"SELECT a.* FROM public.\"Areas\" a JOIN public.\"Regions\" r ON a.\"RegionId\" = r.\"Id\" WHERE r.\"TripId\" = {tripId} ORDER BY a.\"Id\" FOR UPDATE OF a").AsNoTracking().ToListAsync(token);
            await _dbContext.Regions.FromSqlInterpolated($"SELECT * FROM public.\"Regions\" WHERE \"TripId\" = {tripId} ORDER BY \"Id\" FOR UPDATE").AsNoTracking().ToListAsync(token);
            await _dbContext.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} FOR UPDATE").AsNoTracking().ToListAsync(token);
        }
        var current = await NativeTripGraph.Query(_dbContext).SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == owner, token);
        if (current == null) throw new InvalidOperationException("Trip not found or not yours for upsert.");
        if (current.Regions.Any(r => r.UserId != owner || r.Places.Any(p => p.UserId != owner))
            || current.Segments.Any(s => s.UserId != owner)) throw new TripImportValidationException("The current graph requires reconciliation.");
        await RequireActiveImportOwnerAsync(owner, token);
        return current;
    }

    /// <summary>Account revocation is checked under a user barrier after all content lock waits.</summary>
    private async Task RequireActiveImportOwnerAsync(string owner, CancellationToken token)
    {
        var active = _dbContext.Database.IsRelational()
            ? (await _dbContext.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {owner} AND \"IsActive\" FOR SHARE").AsNoTracking().ToListAsync(token)).Count > 0
            : await _dbContext.Users.AsNoTracking().AnyAsync(u => u.Id == owner && u.IsActive, token);
        if (!active) throw new InvalidOperationException("Import authority is no longer valid.");
    }


}
