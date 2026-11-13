using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wayfarer.Services;

/// <summary>One reviewed atomic content/decision action. Items follow reviewed block order; dependencies precede routes.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCompositionAction(string Source, IReadOnlyList<TripCompositionItem> Items);

/// <summary>Explicit copy/reuse/skip/remove choice. Source identities never confer current destination authority.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCompositionItem(string Kind, Guid SourceId, string Decision,
    Guid? DestinationId = null, Guid? RegionId = null, string? NewRegionName = null,
    string? Name = null, bool Separate = false, bool Readd = false,
    TripCompositionMappingOriginal? ExpectedDecision = null,
    TripCompositionFields? Fields = null, TripCompositionPlacement? Placement = null,
    IReadOnlyList<TripCompositionItem>? Children = null,
    IReadOnlyList<TripCompositionAnchor>? Anchors = null,
    JsonElement Original = default, string? ConditionToken = null,
    string? Places = null, string? Areas = null,
    IReadOnlyList<TripCompositionOrderOriginal>? ExpectedRegions = null, TripCompositionPlacement? RegionPlacement = null, string? RouteClearToken = null, Guid? ExpectedParentId = null);

/// <summary>Current durable decision original, including removed destination identity; null means unresolved.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCompositionMappingOriginal(Guid DestinationId, string Decision);

/// <summary>Approved route anchor identity and exact current location/parent. A copied dependency names an earlier item by source ID.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCompositionAnchor(Guid SourceId, Guid? DestinationId, Guid? RegionId, [property: JsonNumberHandling(JsonNumberHandling.Strict)] double[]? Location);

/// <summary>Only named product groups are selectable. Omission keeps current destination values.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCompositionFields(
    TripCompositionField? Name = null, TripCompositionField? Title = null, TripCompositionField? Notes = null,
    TripCompositionField? IconName = null, TripCompositionField? MarkerColor = null, TripCompositionField? FillHex = null,
    TripCompositionField? Cover = null, TripCompositionField? Address = null, TripCompositionField? Location = null,
    TripCompositionField? Geometry = null, TripCompositionField? Center = null, TripCompositionField? Route = null,
    TripCompositionField? Planning = null, TripCompositionField? Tags = null, TripCompositionField? Viewport = null);

/// <summary>Use source, authored edit or explicit Clear with the relevant exact current original, including explicit null.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCompositionField(string Operation, JsonElement Expected, JsonElement Value = default);

/// <summary>Destination parent and position are independent. Originals cover the affected list's identities and stored orders.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCompositionPlacement(IReadOnlyList<TripCompositionOrderOriginal> Expected,
    string Position = "append", Guid? AfterId = null);

/// <summary>Exact nullable order original; list parent is independently validated under current ownership.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCompositionOrderOriginal(Guid Id, int? DisplayOrder);

/// <summary>Strict bounded transport and the established style catalog shared by cookie and bearer routes.</summary>
internal static class TripCompositionInput
{
    /// <summary>Runs established cache/thumbnail effects after the database commit; private-only actions do not schedule them.</summary>
    internal static async Task<object> ApplyWithFreshnessAsync(Task<object> save, ITripMapThumbnailGenerator thumbnails, ICacheWarmupScheduler warmup)
    {
        var result = await save;
        if (result is TripCompositionApplied { PublicChanged: true } applied)
        {
            thumbnails.InvalidateThumbnails(applied.TripId, applied.UpdatedAt);
            await warmup.ScheduleWarmupAsync(applied.TripId);
        }
        return result;
    }

    internal static TripCompositionAction Parse(JsonElement request)
    {
        Models.Dtos.PlaceStructuralRequest.RejectDuplicateMembers(request);
        return request.Deserialize<TripCompositionAction>(TripCompositionFieldValues.Wire) ?? throw TripCompositionFieldValues.Invalid();
    }
    internal static IReadOnlySet<string> Icons(IWebHostEnvironment environment)
    {
        if (string.IsNullOrEmpty(environment.WebRootPath)) return new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(environment.WebRootPath, "icons", "wayfarer-map-icons", "dist", "marker");
        return (Directory.Exists(path) ? Directory.GetFiles(path, "*.svg").Select(Path.GetFileNameWithoutExtension).OfType<string>() : [])
            .ToHashSet(StringComparer.Ordinal);
    }
    internal static IReadOnlySet<string> Colors(IIconColorProvider colors) =>
        (colors.GetAvailableColors()?.Backgrounds ?? []).ToHashSet(StringComparer.Ordinal);
}
