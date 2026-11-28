using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using static Wayfarer.Models.Dtos.CreationConditionalRequest;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Applies checked append and Region order through the existing locked owners.</summary>
internal sealed class TripStructureCollections(ApplicationDbContext db)
{
    /// <summary>Appends a generated normal Region and captures exact authored/defaulted fields and sibling order.</summary>
    internal async Task<TripStructureResult> CreateRegionAsync(TripStructureWrite write)
    {
        using var body = TripStructureScalars.Body(write.Payload);
        var request = await ReadAsync(body, false, write.Token);
        var region = write.Web ? TripEditorRegionMutationService.BuildCreationRegion(request) : BearerRegion(request);
        return await new CheckedCreationService(db).RegionAsync(write.TripId, write.Subject, request, region, async (created, _) =>
        {
            string[] fields = ["name", "notes", "coverImage", "center"];
            return new TripStructureResult(true, new
            {
                created.Id, created.DisplayOrder,
                fieldDigests = fields.ToDictionary(field => field, field => TripStructureDigests.Region(created, field)),
                regionOrder = await RegionOrderAsync(db, write.TripId, write.Token)
            });
        }, write.Readmit, write.Token, protectedWrite: true);
    }

    /// <summary>Appends an unlocated or coordinate-supplied bearer Place without provider acquisition.</summary>
    internal async Task<TripStructureResult> CreateBearerPlaceAsync(TripStructureWrite write)
    {
        using var body = TripStructureScalars.Body(write.Payload);
        var request = await ReadAsync(body, true, write.Token);
        ValidateValue(request.Value, true, false);
        var value = Fields(request.Value, ["regionId", "name", "notes", "location", "iconName", "markerColor"]);
        var destination = Required(value, "regionId");
        var regionId = destination.ValueKind == System.Text.Json.JsonValueKind.Null ? (Guid?)null : Id(destination);
        if (regionId != request.RegionId) throw Invalid("The desired destination must match the original destination.");
        var place = new Place
        {
            Name = CreationText(value, "name", false).Trim(), Notes = CreationNotes(value),
            IconName = CreationText(value, "iconName", true), MarkerColor = CreationText(value, "markerColor", true),
            Location = CreationPoint(Required(value, "location"))
        };
        if (string.IsNullOrWhiteSpace(place.IconName)) place.IconName = "marker";
        if (string.IsNullOrWhiteSpace(place.MarkerColor)) place.MarkerColor = "bg-blue";
        return await CreatePlaceAsync(write, request, place, "notRequested", "none");
    }

    /// <summary>Reports the generated child and actual canonical fallback only when the transaction created it.</summary>
    internal Task<TripStructureResult> CreatePlaceAsync(TripStructureWrite write, CreationConditionalRequest request, Place place,
        string geocode, string tupleState) =>
        new CheckedCreationService(db).PlaceAsync(write.TripId, write.Subject, request, place, async created =>
        {
            var saved = await db.Places.AsNoTracking().SingleAsync(row => row.Id == created.Id, write.Token);
            var fallback = request.RegionId == null ? saved.RegionId : (Guid?)null;
            string[] fields = ["name", "notes", "iconName", "markerColor", "address", "location"];
            return new TripStructureResult(true, new
            {
                saved.Id, saved.RegionId, saved.DisplayOrder,
                fieldDigests = fields.ToDictionary(field => field, field => TripStructureDigests.Place(saved, field)),
                placeOrder = await PlaceOrderAsync(db, saved.RegionId, write.Token),
                createdFallbackRegionId = fallback,
                regionOrder = fallback.HasValue ? await RegionOrderAsync(db, write.TripId, write.Token) : null,
                addressEffect = TripStructureDigests.AddressEffect(saved, geocode, tupleState)
            });
        }, write.Readmit, write.Token, protectedWrite: true);

    /// <summary>Uses the existing reviewed private Region conditions and returns the committed full vector.</summary>
    internal async Task<TripStructureResult> OrderRegionsAsync(TripStructureWrite write, LifecycleDependencyConfirmation confirmation)
    {
        using var body = TripStructureScalars.Body(write.Payload);
        var request = await RegionOrderRequest.ReadAsync(body, write.Token);
        await new RegionOrderingService(db, confirmation).ApplyAsync(write.TripId, write.Subject, request, write.Web,
            write.Readmit, write.Token, protectedWrite: true);
        var expected = request.Regions.EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetGuid(), row => row.GetProperty("displayOrder").GetInt32());
        var changed = request.RegionIds.Where((id, index) => expected[id] != index + 1).Any();
        return new(changed, new { regionOrder = await RegionOrderAsync(db, write.TripId, write.Token) });
    }

    /// <summary>Returns every persisted Region order and exact canonical marker at commit.</summary>
    internal static async Task<object> RegionOrderAsync(ApplicationDbContext db, Guid tripId, CancellationToken token) =>
        await db.Regions.AsNoTracking().Where(row => row.TripId == tripId).OrderBy(row => row.DisplayOrder).ThenBy(row => row.Id)
            .Select(row => new { row.Id, row.DisplayOrder, isCanonical = row.Name == "Unassigned Places" }).ToArrayAsync(token);

    /// <summary>Returns nullable stored Place orders in the existing null-last display sequence.</summary>
    internal static async Task<object> PlaceOrderAsync(ApplicationDbContext db, Guid regionId, CancellationToken token) =>
        new
        {
            regionId,
            items = await db.Places.AsNoTracking().Where(row => row.RegionId == regionId).OrderBy(row => row.DisplayOrder == null)
                .ThenBy(row => row.DisplayOrder).ThenBy(row => row.Id).Select(row => new { row.Id, row.DisplayOrder }).ToArrayAsync(token)
        };

    /// <summary>Preserves the existing bearer defaults and reserved-name validation.</summary>
    private static Region BearerRegion(CreationConditionalRequest request)
    {
        ValidateValue(request.Value, false, false);
        var value = Fields(request.Value, ["name", "notes"]);
        var name = CreationText(value, "name", false).Trim();
        if (name.Equals("Unassigned Places", StringComparison.OrdinalIgnoreCase)) throw Invalid("Region name is reserved.");
        return new() { Name = name, Notes = CreationNotes(value) };
    }
}
