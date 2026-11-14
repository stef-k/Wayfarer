using System.Data;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

/// <summary>One authorized personal download, never a generated/shared cache artifact.</summary>
public sealed record PersonalNativeExport(string Name, string Kml);

public partial class TripExportService
{
    /// <summary>Reads authority, complete graph and private planning in one repeatable owner snapshot.</summary>
    public async Task<PersonalNativeExport> GeneratePersonalNativeAsync(Guid tripId, string owner,
        bool includePrivatePlanning, CancellationToken cancellationToken = default)
        => await GeneratePersonalNativeCoreAsync(tripId, owner, includePrivatePlanning, null, cancellationToken);

    /// <summary>Bearer native download rechecks the incoming credential before returning its buffered file.</summary>
    public Task<PersonalNativeExport> GeneratePersonalNativeForBearerAsync(Guid tripId, string owner, bool includePrivatePlanning,
        Func<CancellationToken, Task<bool>> credential, CancellationToken token) =>
        GeneratePersonalNativeCoreAsync(tripId, owner, includePrivatePlanning, credential, token);

    private async Task<PersonalNativeExport> GeneratePersonalNativeCoreAsync(Guid tripId, string owner, bool includePrivatePlanning,
        Func<CancellationToken, Task<bool>>? credential, CancellationToken cancellationToken)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken) : null;
        if (credential != null) await LocationDeletionFence.AcquireAsync(_db, owner, cancellationToken);
        // A locking active-owner read rejects an older snapshot after a revocation wait.
        var active = _db.Database.IsNpgsql()
            ? (await _db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {owner} AND \"IsActive\" FOR SHARE").AsNoTracking().ToListAsync(cancellationToken)).Count == 1
            : await _db.Users.AsNoTracking().AnyAsync(u => u.Id == owner && u.IsActive, cancellationToken);
        var trip = active ? await NativeTripGraph.Query(_db).SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == owner, cancellationToken) : null;
        if (trip == null) throw new UnauthorizedAccessException("The personal download is unavailable.");
        var kml = TripWayfarerKmlExporter.BuildKml(trip, includePrivatePlanning);
        if (credential != null && !await credential(cancellationToken)) throw new UnauthorizedAccessException("The personal download is unavailable.");
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return new(trip.Name, kml);
    }
}
