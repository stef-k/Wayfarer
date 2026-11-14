using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Plain content originals, not grants or replay keys. Only selected objects/dependencies are compared.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCopyOriginal(string Kind, Guid Id, string Value, string? Children = null);

/// <summary>A live-source copy has no workspace, retained snapshot or implicit Trip metadata adoption.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TripCopyRequest(Guid SourceTripId, Guid DestinationTripId, string Context,
    IReadOnlyList<TripCopyOriginal> Originals, IReadOnlyList<TripCompositionItem> Items);

/// <summary>Computes originals from the authorized projection only; foreign planning/media never enters hashes.</summary>
internal static class TripCopyOriginals
{
    internal static (string Context, TripCopyOriginal[] Objects) Capture(Trip source, bool own, object projection)
    {
        var root = JsonSerializer.SerializeToNode(projection, TripCompositionSnapshot.Json)!;
        var graph = root["graph"]!;
        var objects = new List<TripCopyOriginal>();
        foreach (var region in graph["regions"]!.AsArray())
        {
            var metadata = region!.DeepClone().AsObject();
            metadata.Remove("places"); metadata.Remove("areas");
            Add("region", metadata, source.Id, Hash(new { places = region["places"], areas = region["areas"] }));
            foreach (var place in region["places"]!.AsArray()) Add("place", place!, Guid.Parse(region["id"]!.ToString()));
            foreach (var area in region["areas"]!.AsArray()) Add("area", area!, Guid.Parse(region["id"]!.ToString()));
        }
        foreach (var segment in graph["segments"]!.AsArray()) Add("segment", segment!, source.Id);
        return (Hash(new { own, policy = source.ContributionPolicy, source.Credits, title = own ? null : source.Name }), objects.ToArray());

        void Add(string kind, JsonNode value, Guid parent, string? children = null)
        {
            var id = Guid.Parse(value["id"]!.ToString());
            var planning = own ? root["planning"]![$"{kind}s"]?[id.ToString()] : null;
            objects.Add(new(kind, id, Hash(new { parent, value, planning }), children));
        }
    }

    internal static void Validate(TripCopyRequest request, string context, IReadOnlyList<TripCopyOriginal> current)
    {
        if (request.Context != context) throw Stale();
        if (request.Originals == null || request.Originals.Count > 10000 || request.Originals.Any(o => o == null)
            || request.Originals.Select(o => (o.Kind, o.Id)).Distinct().Count() != request.Originals.Count) throw TripCompositionSource.Invalid();
        foreach (var item in Flatten(request.Items))
        {
            var actual = Require(item.Kind, item.SourceId);
            if (item.Children != null && request.Originals.Single(o => o.Kind == item.Kind && o.Id == item.SourceId).Children != actual.Children) throw Stale();
            foreach (var anchor in item.Anchors ?? []) Require("place", anchor.SourceId);
        }
        TripCopyOriginal Require(string kind, Guid id)
        {
            var actual = current.SingleOrDefault(o => o.Kind == kind && o.Id == id);
            var reviewed = request.Originals.SingleOrDefault(o => o.Kind == kind && o.Id == id);
            if (actual == null || reviewed?.Value != actual.Value) throw Stale();
            return actual;
        }
    }

    internal static IEnumerable<TripCompositionItem> Flatten(IReadOnlyList<TripCompositionItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(i.Children ?? []));
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, TripCompositionSnapshot.Json)));
    private static TripMetadataMutationRejected Stale() => new(409, "copy-source-stale", "The reviewed source content or terms changed. Keep your choices and explicitly refresh and review the source before saving.");
}
