using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

internal sealed partial class TripCompositionApply
{
    /// <summary>Removal joins existing loss/consequence owners and leaves retained notices and source values intact.</summary>
    private async Task RemoveAsync(TripCompositionItem item, CancellationToken token)
    {
        if (item.Kind == "trip") throw Invalid("Use the ordinary Delete Trip C action to discard the destination and its workspace.");
        var target = TripCompositionSource.Target(destination, item.Kind, item.DestinationId!.Value);
        if (item.Fields != null || item.Children != null || blocks.Count != 0) throw Invalid("Review removal separately from new sibling insertion.");
        if (item.Kind is "place" or "region")
        {
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            await lifecycle.CompositionRemoveAsync(destination.Id, destination.UserId, item, token);
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            destination = await db.Trips.Include(t => t.Tags).Include(t => t.Regions).ThenInclude(r => r.Places)
                .Include(t => t.Regions).ThenInclude(r => r.Areas).Include(t => t.Segments).ThenInclude(s => s.Waypoints)
                .AsSplitQuery().SingleAsync(t => t.Id == destination.Id, token);
            return;
        }
        // Leaf removal compares the full reviewed content plus its complete private planning original.
        var expected = new
        {
            content = item.Kind == "area" ? (object)((Area)target).ToApiDto() : TripCompositionFieldValues.Route((Segment)target),
            planning = ((Models.Planning.IPlanningTarget)target).Planning,
            notes = TripCompositionFieldValues.Current(target, "notes"),
            title = target is Segment segment ? segment.Title : null
        };
        if (!TripCompositionFieldValues.Equal(item.Original, expected)) throw TripCompositionOrdering.Stale();
        if (target is Area area)
        {
            destination.Regions.Single(r => r.Id == area.RegionId).Areas.Remove(area); db.Areas.Remove(area);
        }
        else if (target is Segment removedSegment) { destination.Segments.Remove(removedSegment); db.Segments.Remove(removedSegment); }
        else throw Invalid("This object cannot be removed through composition.");
    }
}
