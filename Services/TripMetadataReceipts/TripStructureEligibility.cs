using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Locks an owned typed chain without performing domain work or acquiring the operation fence.</summary>
internal static class TripStructureEligibility
{
    /// <summary>Holds child-before-parent share locks through permit issuance.</summary>
    internal static async Task RequireAsync(ApplicationDbContext db, string subject, Guid tripId, TripReceiptTarget target,
        string kind, bool web, CancellationToken token)
    {
        var regionId = target.Type switch
        {
            "area" => (await db.Areas.FromSqlInterpolated($"""
                SELECT * FROM public."Areas" WHERE "Id" = {target.Id} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token))?.RegionId ?? throw Missing(),
            "place" => (await db.Places.FromSqlInterpolated($"""
                SELECT * FROM public."Places" WHERE "Id" = {target.Id} AND "UserId" = {subject} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token))?.RegionId ?? throw Missing(),
            "region" => target.Id,
            "trip" when target.Id == tripId => (Guid?)null,
            _ => throw Missing()
        };

        if (regionId is { } parent)
        {
            var region = await db.Regions.FromSqlInterpolated($"""
                SELECT * FROM public."Regions" WHERE "Id" = {parent} AND "TripId" = {tripId}
                    AND "UserId" = {subject} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw Missing();
            if (web && kind == "region-fields" && region.Name.Equals("Unassigned Places", StringComparison.OrdinalIgnoreCase))
                throw new TripMetadataMutationRejected(403, "mutation-region-protected", "The reserved Region cannot be edited by this operation.");
            if (region.Name == "Unassigned Places" && (kind is "region-delete" or "area-create" || web && kind == "place-order"))
                throw new TripMetadataMutationRejected(403, "mutation-operation-forbidden", "This operation is unavailable in Unassigned Places.");
        }
        var trips = await db.Trips.FromSqlInterpolated($"""
            SELECT * FROM public."Trips" WHERE "Id" = {tripId} AND "UserId" = {subject} FOR SHARE
            """).AsNoTracking().ToListAsync(token);
        if (trips.Count != 1) throw Missing();
    }

    /// <summary>Responds identically to missing and foreign scope.</summary>
    private static TripMetadataMutationRejected Missing() => new(404, "mutation-scope-unavailable", "Trip edit scope not found.");
}
