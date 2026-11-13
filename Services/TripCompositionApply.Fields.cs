using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Planning;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services;

internal sealed partial class TripCompositionApply
{
    /// <summary>Tag membership is public content but EF's join rows are outside the scalar freshness traversal.</summary>
    internal bool TagsChanged { get; private set; }

    private async Task FieldsAsync(TripCompositionItem item, object original, object target, bool copying, CancellationToken token)
    {
        var fields = TripCompositionFieldValues.Selected(item.Fields).ToArray();
        if (target is Region { Name: "Unassigned Places" } && fields.Length != 0) throw Invalid("Canonical Unassigned metadata cannot be adopted.");
        // Compare every selected group before staging any. Omitted groups never become originals or writes.
        foreach (var (key, field) in fields)
        {
            if (key == "planning" && field.Operation == "useSource" && !source.Own) throw TripCompositionFieldValues.Invalid();
            var currentValue = TripCompositionFieldValues.Current(target, key);
            var matches = copying || (key == "planning"
                ? field.Expected.ValueKind != JsonValueKind.Undefined && PlanningCodec.Equal(ReadPlanning(field.Expected), (PlanningAggregate?)currentValue)
                : TripCompositionFieldValues.Equal(field.Expected, currentValue));
            if (!matches)
                throw new TripMetadataMutationRejected(409, "composition-field-stale", "A selected field changed. Keep the choices and explicitly review the current selected originals.");
        }
        foreach (var (key, field) in fields.OrderBy(f => f.Key == "location" ? 0 : f.Key == "route" ? 1 : 2))
        {
            if (key == "route")
            {
                if (target is not Segment segment || original is not Segment sourceSegment) throw TripCompositionFieldValues.Invalid();
                await db.SaveChangesAsync(token);
                if (field.Operation == "useSource") await CopySegmentAsync(item, sourceSegment, segment, token);
                else await EditRouteAsync(item, field, segment, token);
            }
            else
            {
                var value = TripCompositionFieldValues.Desired(field, original, key);
                switch (key)
                {
                    case "planning":
                        var aggregate = ReadPlanning(value);
                        var planningTarget = (IPlanningTarget)target;
                        if (aggregate != null)
                        {
                            try { PlanningValidation.Validate(aggregate, item.Kind, planningTarget.Planning); }
                            catch (PlanningValidationException error) { throw new TripMetadataMutationRejected(400, "planning-value-invalid", error.Message); }
                        }
                        planningTarget.Planning = field.Operation == "useSource" ? WayfarerKmlAggregateMapper.CopyPlanning(aggregate, true) : aggregate;
                        break;
                    case "address":
                        Address((Place)target, value, field.Operation == "useSource"); break;
                    case "tags":
                        await TagsAsync((Trip)target, value, field.Operation == "useSource", token); break;
                    case "location":
                        var place = (Place)target;
                        var location = TripCompositionFieldValues.Coordinate(value);
                        if (location == null && field.Operation == "clear") throw TripCompositionFieldValues.Invalid();
                        if (copying) place.Location = location;
                        else
                        {
                            await db.SaveChangesAsync(token);
                            await lifecycle.CompositionCoordinatesAsync(destination.Id, place, location, item.ConditionToken, token);
                        }
                        break;
                    default: TripCompositionFieldValues.Apply(target, key, value, icons, colors); break;
                }
            }
            if (field.Operation == "useSource" && key != "planning") AddCredits();
        }
    }

    private static PlanningAggregate? ReadPlanning(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.GetRawText().Length > 262144) throw TripCompositionFieldValues.Invalid();
        return PlanningCodec.Read(value.GetRawText());
    }

    /// <summary>Only captured tuples may retain provider authority. Authored addresses explicitly clear that authority.</summary>
    private static void Address(Place p, JsonElement value, bool captured)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            p.Address = null; p.ResolvedFeatureName = null; p.ResolvedFeatureType = null;
            p.AddressEnrichmentProvider = null; p.AddressEnrichmentStorageMode = null; p.AddressEnrichedAt = null; return;
        }
        var allowed = captured ? new[] { "address", "resolvedFeatureName", "resolvedFeatureType", "addressEnrichmentProvider", "addressEnrichmentStorageMode", "addressEnrichedAt" } : new[] { "address" };
        var members = Models.Dtos.PlaceStructuralRequest.Fields(value, allowed);
        if (!members.TryGetValue("address", out var address) || address.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || address.GetString()?.Contains('\0') == true)
            throw TripCompositionFieldValues.Invalid();
        var metadata = captured ? ResolvedFeatureMetadata.NormalizePersisted(value.GetProperty("resolvedFeatureName").GetString(),
            value.GetProperty("resolvedFeatureType").GetString(), value.GetProperty("addressEnrichmentProvider").GetString(),
            value.GetProperty("addressEnrichmentStorageMode").GetString(), value.GetProperty("addressEnrichedAt").Deserialize<DateTimeOffset?>()) : default;
        p.Address = address.GetString(); p.ResolvedFeatureName = metadata.Name; p.ResolvedFeatureType = metadata.Type;
        p.AddressEnrichmentProvider = metadata.Provider; p.AddressEnrichmentStorageMode = metadata.StorageMode; p.AddressEnrichedAt = metadata.EnrichedAt;
    }

    private async Task TagsAsync(Trip trip, JsonElement value, bool captured, CancellationToken token)
    {
        var values = value.ValueKind == JsonValueKind.Null ? [] : value.Deserialize<string[]>() ?? throw TripCompositionFieldValues.Invalid();
        var names = captured ? values.Select(slug => source.Trip.Tags.SingleOrDefault(t => t.Slug == slug)?.Name
            ?? throw TripCompositionFieldValues.Invalid()).ToArray() : values;
        if (names.Length > 25 || names.Any(n => !TripTagService.IsValidTagName(n))) throw TripCompositionFieldValues.Invalid();
        var previous = trip.Tags.Select(t => t.Slug).ToHashSet(StringComparer.Ordinal);
        trip.Tags.Clear();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = name.Trim().Normalize(System.Text.NormalizationForm.FormC);
            var slug = captured ? source.Trip.Tags.Single(t => t.Name == name).Slug : TripTagService.NormalizeSlug(normalized);
            var tag = db.Tags.Local.SingleOrDefault(t => t.Slug == slug) ?? await db.Tags.SingleOrDefaultAsync(t => t.Slug == slug || t.Name == normalized, token);
            if (tag == null) { tag = new Tag { Id = Guid.NewGuid(), Slug = slug, Name = normalized }; db.Tags.Add(tag); }
            if (!trip.Tags.Contains(tag)) trip.Tags.Add(tag);
        }
        TagsChanged |= !previous.SetEquals(trip.Tags.Select(t => t.Slug));
    }
}
