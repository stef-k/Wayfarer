using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Adapts the protected scalar bodies to the existing locked writers and captures their saved values.</summary>
internal sealed class TripStructureScalars(ApplicationDbContext db)
{
    /// <summary>Applies a Region scalar request under the outer receipt transaction.</summary>
    internal async Task<TripStructureResult> RegionAsync(TripStructureWrite write)
    {
        using var body = Body(write.Payload);
        var result = await new RegionConditionalMutationService(db).ApplyAsync(write.TripId, write.TargetId,
            write.Subject, body, write.Web, write.Readmit, write.Token, protectedWrite: true);
        return new(result.Applied, new
        {
            id = result.Region.Id, regionId = (Guid?)null,
            fieldDigests = Fields(write.Payload).ToDictionary(field => field, field => TripStructureDigests.Region(result.Region, field))
        });
    }

    /// <summary>Applies a Place scalar request and captures the actual saved Address tuple.</summary>
    internal async Task<TripStructureResult> PlaceAsync(TripStructureWrite write, IWebHostEnvironment environment, IIconColorProvider colors)
    {
        using var body = Body(write.Payload);
        var result = await new PlaceConditionalMutationService(db, environment, colors).ApplyAsync(write.TripId, write.TargetId,
            write.Subject, body, write.Web, write.Readmit, write.Token, protectedWrite: true);
        var place = await db.Places.AsNoTracking().SingleAsync(item => item.Id == result.Place.Id, write.Token);
        var fieldDigests = Fields(write.Payload).ToDictionary(field => field, field => TripStructureDigests.Place(place, field));
        return new(result.Applied, write.Web
            ? new { place.Id, place.RegionId, fieldDigests }
            : new
            {
                place.Id, place.RegionId, fieldDigests,
                addressEffect = fieldDigests.ContainsKey("address")
                    ? TripStructureDigests.AddressEffect(place, "notRequested", result.AddressCleared ? "cleared" : "retained")
                    : null
            });
    }

    /// <summary>Shares the existing Area conditional writer for scalar and single-geometry edits.</summary>
    internal async Task<TripStructureResult> AreaAsync(TripStructureWrite write, bool geometryOnly = false)
    {
        var payload = write.Payload;
        if (geometryOnly)
        {
            if (payload.EnumerateObject().Count() != 2 || !payload.TryGetProperty("expected", out var expected)
                || !payload.TryGetProperty("geometry", out var geometry))
                throw TripStructureBodies.Invalid();
            payload = JsonSerializer.SerializeToElement(new { expected, changes = new { geometry } });
        }

        using var body = Body(payload);
        var result = await new AreaConditionalMutationService(db).ApplyAsync(write.TripId, write.TargetId,
            write.Subject, body, write.Web, write.Readmit, write.Token, protectedWrite: true);
        return new(result.Applied, new
        {
            result.Area.Id, result.Area.RegionId,
            fieldDigests = Fields(payload).ToDictionary(field => field, field => TripStructureDigests.Area(result.Area, field))
        });
    }

    /// <summary>Enumerates effective submitted fields after the domain parser accepts the request.</summary>
    private static IEnumerable<string> Fields(JsonElement payload) => payload.GetProperty("changes").EnumerateObject()
        .Where(field => field.Name is not ("notes" or "notesHtml") || field.Value.ValueKind != JsonValueKind.Null)
        .Select(field => field.Name == "notesHtml" ? "notes" : field.Name);

    /// <summary>Provides the parsed JSON without its transport marker to the incumbent binder.</summary>
    internal static MemoryStream Body(JsonElement payload) => new(JsonSerializer.SerializeToUtf8Bytes(payload));
}
