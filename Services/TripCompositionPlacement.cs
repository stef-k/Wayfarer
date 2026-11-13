using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>List-local conditional insertion; reusable items never enter the new sibling block.</summary>
internal static class TripCompositionOrdering
{
    internal static void Insert(Trip trip, string kind, Guid? parent, IReadOnlyList<Guid> added, TripCompositionPlacement? placement)
    {
        var list = Members(trip, kind, parent).Where(x => !added.Contains(x.Id)).ToArray();
        if (placement == null) throw Invalid("Review the destination list and supply its current membership/order originals.");
        Compare(list.Select(x => new TripCompositionOrderOriginal(x.Id, x.Order)), placement.Expected);
        if (!list.Select(x => x.Id).SequenceEqual(placement.Expected.Select(x => x.Id))) throw Stale();
        var index = placement.Position switch
        {
            "append" when placement.AfterId == null => list.Length,
            "start" when placement.AfterId == null => 0,
            "after" when placement.AfterId.HasValue => Array.FindIndex(list, x => x.Id == placement.AfterId) is var i && i >= 0 ? i + 1 : throw Stale(),
            _ => throw Invalid("Select Append, At start or After a compatible current destination item.")
        };
        if ((long)list.Length + added.Count > int.MaxValue - 1L) throw Invalid("The destination list is too large.");
        var ids = list.Select(x => x.Id).ToList(); ids.InsertRange(index, added);
        var members = Members(trip, kind, parent).ToDictionary(x => x.Id);
        for (var i = 0; i < ids.Count; i++) members[ids[i]].SetOrder(i + 1);
    }

    internal static void Compare(IEnumerable<TripCompositionOrderOriginal> current, IReadOnlyList<TripCompositionOrderOriginal>? expected)
    {
        if (expected == null || !current.OrderBy(x => x.Id).SequenceEqual(expected.OrderBy(x => x.Id))) throw Stale();
    }

    private static IEnumerable<Member> Members(Trip trip, string kind, Guid? parent) => kind switch
    {
        "region" when parent == null => trip.Regions.Where(r => r.Name != "Unassigned Places").OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id)
            .Select(r => new Member(r.Id, r.DisplayOrder, i => r.DisplayOrder = i)),
        "place" => trip.Regions.Single(r => r.Id == parent).Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id)
            .Select(p => new Member(p.Id, p.DisplayOrder, i => p.DisplayOrder = i)),
        "area" => trip.Regions.Single(r => r.Id == parent).Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id)
            .Select(a => new Member(a.Id, a.DisplayOrder, i => a.DisplayOrder = i)),
        "segment" when parent == null => trip.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id)
            .Select(s => new Member(s.Id, s.DisplayOrder, i => s.DisplayOrder = i)),
        _ => throw Invalid("This item type and destination parent are incompatible.")
    };
    private sealed record Member(Guid Id, int? Order, Action<int> SetOrder);
    internal static TripMetadataMutationRejected Stale() => new(409, "composition-destination-stale",
        "The destination membership, order or reference changed. Keep the selection and review current C; nothing was redirected.");
    internal static TripMetadataMutationRejected Invalid(string detail) => new(400, "composition-selection-invalid", detail);
}
