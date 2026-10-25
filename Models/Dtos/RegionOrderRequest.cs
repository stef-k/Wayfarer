using System.Text.Json;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Models.Dtos;

/// <summary>Complete normal-Region originals and intent; canonical identity/order is a separate preserved dependency.</summary>
internal sealed record RegionOrderRequest(JsonElement Regions, JsonElement Canonical, Guid[] RegionIds, string? ConditionToken, bool Reviewed)
{
    /// <summary>Strict operation-specific payload, without inferred originals or duplicate members.</summary>
    internal static async Task<RegionOrderRequest> ReadAsync(Stream body, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = PlaceStructuralRequest.Fields(document.RootElement, "expected", "regionIds", "conditionToken", "reviewed");
            var expected = PlaceStructuralRequest.Fields(root.GetValueOrDefault("expected"), "regions", "canonical");
            if (expected.Count != 2) throw Invalid();
            var regions = expected["regions"];
            if (regions.ValueKind != JsonValueKind.Array) throw Invalid();
            List<Guid> originals = [];
            foreach (var region in regions.EnumerateArray())
            {
                var fields = PlaceStructuralRequest.Fields(region, "id", "displayOrder", "name", "notes", "coverImageUrl", "center");
                if (fields.Count != 6 || !fields["displayOrder"].TryGetInt32(out _) || fields["name"].ValueKind != JsonValueKind.String
                    || new[] { "notes", "coverImageUrl" }.Any(key => fields[key].ValueKind is not (JsonValueKind.String or JsonValueKind.Null))) throw Invalid();
                if (fields["center"].ValueKind != JsonValueKind.Null)
                {
                    var pair = PlaceStructuralRequest.Fields(fields["center"], "latitude", "longitude");
                    if (pair.Count != 2 || pair.Values.Any(v => !v.TryGetDouble(out var number) || !double.IsFinite(number))) throw Invalid();
                }
                originals.Add(CreationConditionalRequest.Id(fields["id"]));
            }
            if (originals.Distinct().Count() != originals.Count) throw Invalid();
            var canonical = expected["canonical"];
            if (canonical.ValueKind != JsonValueKind.Null)
            {
                var fields = PlaceStructuralRequest.Fields(canonical, "id", "displayOrder");
                if (fields.Count != 2 || !fields["displayOrder"].TryGetInt32(out _) || originals.Contains(CreationConditionalRequest.Id(fields["id"]))) throw Invalid();
            }
            var vector = root.GetValueOrDefault("regionIds");
            if (vector.ValueKind != JsonValueKind.Array) throw Invalid();
            var ids = vector.EnumerateArray().Select(CreationConditionalRequest.Id).ToArray();
            if (ids.Distinct().Count() != ids.Length || !ids.Order().SequenceEqual(originals.Order())) throw Invalid();
            if (root.TryGetValue("conditionToken", out var condition) && condition.ValueKind != JsonValueKind.String) throw Invalid();
            if (root.TryGetValue("reviewed", out var reviewed) && reviewed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            return new(regions.Clone(), canonical.Clone(), ids, condition.ValueKind == JsonValueKind.String ? condition.GetString() : null, reviewed.ValueKind == JsonValueKind.True);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { throw Invalid(); }
    }

    /// <summary>Public material originals use published Notes and exact stored cover/order rather than display defaults.</summary>
    internal static object Original(Region region) => new { region.Id, region.DisplayOrder, region.Name,
        Notes = RichNotes.Normalize(region.Notes), region.CoverImageUrl, Center = PlaceStructuralRequest.Pair(region.Center) };

    /// <summary>Canonical content and children are outside this operation; its identity and historical order are protected.</summary>
    internal static object? CanonicalOriginal(Region[] regions) => regions.Where(IsCanonical).Select(r => (object)new { r.Id, r.DisplayOrder }).SingleOrDefault();

    /// <summary>Uses the canonical stored-name predicate independently of historical order.</summary>
    internal static bool IsCanonical(Region region) => region.Name == "Unassigned Places";

    /// <summary>Binds the exact submitted originals, vector and Web/bearer dialect to the prepared conditions.</summary>
    internal string Intent(bool web) => PlaceStructuralRequest.Hash(new { web, Regions, Canonical, RegionIds });

    /// <summary>Nonleaking known-unapplied request rejection.</summary>
    internal static TripMetadataMutationRejected Invalid() => new(400, "mutation-request-invalid", "Provide complete Region originals and a valid normal-Region vector.", new() { ["request"] = ["Complete original Region evidence and a valid vector are required."] });
}
