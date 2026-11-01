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
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken) : null;
        var active = await _db.Users.AsNoTracking().AnyAsync(u => u.Id == owner && u.IsActive, cancellationToken);
        var trip = active ? await NativeTripGraph.Query(_db).SingleOrDefaultAsync(t => t.Id == tripId && t.UserId == owner, cancellationToken) : null;
        if (trip == null) throw new UnauthorizedAccessException("The personal download is unavailable.");
        var kml = TripWayfarerKmlExporter.BuildKml(trip, includePrivatePlanning);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return new(trip.Name, kml);
    }
}
