using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Planning;

namespace Wayfarer.Services;

/// <summary>Reviewed snapshot selection and current-C apply. Construction and domain owners retain field/route/lifecycle authority.</summary>
internal sealed partial class TripCompositionApply(ApplicationDbContext db, Trip current, TripCompositionSource source, string side,
    List<TripCompositionMapping> mappings, PlaceRegionLifecycleService lifecycle, SegmentAggregateTokenService routeTokens,
    SegmentRouteClearConfirmation routeConsent, IReadOnlySet<string> icons, IReadOnlySet<string> colors)
{
    private Trip destination = current;
    private readonly TripCompositionOrderOriginal[] initialRegions = current.Regions.Select(r => new TripCompositionOrderOriginal(r.Id, r.DisplayOrder)).ToArray();
    private readonly Dictionary<(string Kind, Guid? Parent), Block> blocks = [];
    private readonly Dictionary<Guid, Guid> newPlaces = [];
    private readonly HashSet<(string Kind, Guid Source)> selected = [];

    internal async Task ApplyAsync(IReadOnlyList<TripCompositionItem> items, CancellationToken token)
    {
        foreach (var item in items) await ApplyItemAsync(item, null, token);
        foreach (var (list, block) in blocks)
            TripCompositionOrdering.Insert(destination, list.Kind, list.Parent, block.Ids, block.Placement);
    }

    private async Task ApplyItemAsync(TripCompositionItem item, Guid? forcedParent, CancellationToken token)
    {
        if (!selected.Add((item.Kind, item.SourceId)) || item.Decision is not ("copy" or "reuse" or "skip" or "remove"))
            throw Invalid("Each source object needs one explicit copy, reuse, skip or remove decision.");
        if (item.ConditionToken?.Length > 16384 || item.RouteClearToken?.Length > 16384) throw Invalid("Review evidence exceeds the supported token limit.");
        var original = TripCompositionSource.Target(source.Trip, item.Kind, item.SourceId);
        var counterparts = mappings.Where(m => m.Source == side && m.Kind == item.Kind && m.SourceId == item.SourceId).ToArray();
        var previous = item.ExpectedDecision == null ? null : counterparts.SingleOrDefault(m => m.DestinationId == item.ExpectedDecision.DestinationId);
        if (counterparts.Length == 0 ? item.ExpectedDecision != null : previous == null
            || item.ExpectedDecision != new TripCompositionMappingOriginal(previous.DestinationId, previous.Decision))
            throw new TripMetadataMutationRejected(409, "composition-decision-stale", "The saved decision changed. Review its current counterpart before another action.");
        if (previous?.Decision is "skip" or "destinationRemoved" && item.Decision is "copy" or "reuse" && !item.Readd)
            throw Invalid("Deliberately review and select Re-add before restoring an excluded idea.");
        if (item.Decision == "skip")
        {
            if (counterparts.Any(m => m.Decision is not ("skip" or "destinationRemoved"))) throw Invalid("Use reviewed Remove to exclude content already included in C.");
            if (item.Fields != null || item.Children != null) throw Invalid("Skip cannot adopt fields or children.");
            SaveDecision(item, previous?.DestinationId ?? Guid.Empty, "skip"); return;
        }
        if (item.Decision == "remove")
        {
            if (previous == null || previous.DestinationId != item.DestinationId || previous.Decision is "skip" or "destinationRemoved")
                throw Invalid("Select the current included counterpart for removal.");
            await RemoveAsync(item, token);
            SaveDecision(item, previous.DestinationId, "destinationRemoved"); return;
        }
        object target;
        var copying = item.Decision == "copy";
        if (copying)
        {
            if (item.Kind == "trip" || item.DestinationId.HasValue) throw Invalid("Trips use selected metadata adoption; copies receive fresh identities.");
            if (counterparts.Any(m => m.Decision is not ("skip" or "destinationRemoved")) && !item.Separate)
                throw Invalid("This idea is already included. Explicitly review an additional separate copy.");
            target = await CopyAsync(item, original, forcedParent, token);
            AddCredits();
        }
        else
        {
            if (!item.DestinationId.HasValue || item.RegionId.HasValue || item.NewRegionName != null || item.Placement != null || item.Name != null)
                throw Invalid("Reuse selects a current counterpart and keeps its parent and position.");
            try { target = TripCompositionSource.Target(destination, item.Kind, item.DestinationId.Value); }
            catch (TripMetadataMutationRejected) { throw TripCompositionOrdering.Stale(); }
            var currentParent = target switch { Place p => (Guid?)p.RegionId, Area a => a.RegionId, _ => null };
            if (currentParent.HasValue && currentParent != item.ExpectedParentId) throw TripCompositionOrdering.Stale();
        }
        await FieldsAsync(item, original, target, copying, token);
        SaveDecision(item, TripCompositionMatching.Id(target), copying && item.Separate ? "separate" : copying ? "included" : "reuse");
        if (item.Children == null) return;
        if (original is not Region region || target is not Region parent || item.Children.Count > 128) throw Invalid("Only a Region can include direct children.");
        var children = region.Places.Select(p => ("place", p.Id)).Concat(region.Areas.Select(a => ("area", a.Id))).ToHashSet();
        if (item.Children.Count != children.Count || item.Children.Any(c => c == null || !children.Contains((c.Kind, c.SourceId)) || c.Children != null))
            throw Invalid("Review each direct Place and Area with an individual copy, reuse or skip decision. Segments are separate.");
        foreach (var child in item.Children) await ApplyItemAsync(child, parent.Id, token);
    }

    /// <summary>Reuses the direct constructors also used by full construction; destination identity/container is always server-owned.</summary>
    private async Task<object> CopyAsync(TripCompositionItem item, object original, Guid? forcedParent, CancellationToken token)
    {
        if (item.Name != null && string.IsNullOrWhiteSpace(item.Name)) throw Invalid("A selected custom name/title must contain text.");
        var keepPlanning = side == "a" && source.Own;
        var name = item.Name ?? TripCompositionMatching.Name(original);
        object target;
        Guid? parent = null;
        switch (original)
        {
            case Region r:
                if (r.Name == "Unassigned Places") throw Invalid("Copy selected Places to C's canonical Unassigned category; it is not a normal Region to duplicate.");
                if (item.RegionId.HasValue || item.NewRegionName != null) throw Invalid("Region placement uses the Trip's normal Region list.");
                var region = TripCloneCoordinator.CopyRegion(r, destination.Id, destination.UserId, keepPlanning);
                region.Name = TripCompositionFieldValues.Name(name, true); destination.Regions.Add(region); db.Regions.Add(region); target = region; break;
            case Place p:
                var placeParent = Parent(item, forcedParent, false);
                var place = TripCloneCoordinator.CopyPlace(p, placeParent.Id, destination.UserId, keepPlanning);
                place.Name = TripCompositionFieldValues.Name(name, false); placeParent.Places.Add(place); db.Places.Add(place);
                parent = placeParent.Id; target = place; newPlaces.Add(p.Id, place.Id); break;
            case Area a:
                var areaParent = Parent(item, forcedParent, true);
                var area = TripCloneCoordinator.CopyArea(a, areaParent.Id, keepPlanning);
                area.Name = TripCompositionFieldValues.Name(name, false); areaParent.Areas.Add(area); db.Areas.Add(area);
                parent = areaParent.Id; target = area; break;
            case Segment s:
                if (item.RegionId.HasValue || item.NewRegionName != null) throw Invalid("Segments belong to C's Trip-level list.");
                await db.SaveChangesAsync(token); // Approved dependencies must be visible to the existing route core.
                var segment = await CopySegmentAsync(item, s, null, token);
                segment.Planning = keepPlanning ? WayfarerKmlAggregateMapper.CopyPlanning(s.Planning, true) : null;
                segment.Title = item.Name == null ? s.Title : TripCreditCodec.Title(item.Name);
                target = segment; break;
            default: throw Invalid("Select a Region, Place, Area or Segment.");
        }
        name = TripCompositionMatching.Name(target);
        var sameNames = item.Kind switch
        {
            "region" => destination.Regions.Count(r => r.Name == name),
            "place" => destination.Regions.Single(r => r.Id == parent).Places.Count(p => p.Name == name),
            "area" => destination.Regions.Single(r => r.Id == parent).Areas.Count(a => a.Name == name),
            "segment" => destination.Segments.Count(s => s.Title != null && s.Title == ((Segment)target).Title),
            _ => 0
        };
        if (sameNames > 1 && !item.Separate) throw Invalid("A same-name object exists. Review reuse or explicitly choose a separate object.");
        var placement = item.Placement;
        if (forcedParent.HasValue && placement == null) placement = new([], "append");
        AddBlock(item.Kind, parent, TripCompositionMatching.Id(target), placement);
        return target;
    }

    private Region Parent(TripCompositionItem item, Guid? forced, bool area)
    {
        if (forced.HasValue && item.RegionId.HasValue && item.RegionId != forced || forced.HasValue && item.NewRegionName != null)
            throw Invalid("Resolve the conflicting child destination Region.");
        if (item.NewRegionName != null)
        {
            if (item.RegionId.HasValue) throw Invalid("Select an existing Region or explicitly create one.");
            TripCompositionOrdering.Compare(initialRegions, item.ExpectedRegions);
            var created = new Region
            {
                Id = Guid.NewGuid(),
                UserId = destination.UserId,
                TripId = destination.Id,
                Name = TripCompositionFieldValues.Name(item.NewRegionName, true)
            };
            if (destination.Regions.Any(r => r.Name == created.Name) && !item.Separate) throw Invalid("A same-name Region exists. Explicitly choose a separate normal Region.");
            destination.Regions.Add(created); db.Regions.Add(created);
            AddBlock("region", null, created.Id, item.RegionPlacement);
            return created;
        }
        var id = forced ?? item.RegionId;
        if (id.HasValue)
        {
            var region = destination.Regions.SingleOrDefault(r => r.Id == id) ?? throw TripCompositionOrdering.Stale();
            if (area && region.Name == "Unassigned Places") throw Invalid("Areas require a normal destination Region.");
            return region;
        }
        if (area) throw Invalid("Select or explicitly create a normal Region for the Area.");
        TripCompositionOrdering.Compare(initialRegions, item.ExpectedRegions);
        var canonical = destination.Regions.SingleOrDefault(r => r.Name == "Unassigned Places");
        if (canonical != null) return canonical;
        canonical = new Region { Id = Guid.NewGuid(), TripId = destination.Id, UserId = destination.UserId, Name = "Unassigned Places", DisplayOrder = 0 };
        destination.Regions.Add(canonical); db.Regions.Add(canonical); return canonical;
    }

    private void AddBlock(string kind, Guid? parent, Guid id, TripCompositionPlacement? placement)
    {
        var key = (kind, parent);
        if (!blocks.TryGetValue(key, out var block)) blocks[key] = block = new([], placement);
        else if (!JsonElement.DeepEquals(TripCompositionFieldValues.Element(block.Placement), TripCompositionFieldValues.Element(placement)))
            throw Invalid("New siblings in one list require one shared reviewed placement.");
        block.Ids.Add(id);
    }

    private void SaveDecision(TripCompositionItem item, Guid destinationId, string decision)
    {
        // Retained copies are current product counterparts, including initial A identities. Re-add replaces its removed choice.
        mappings.RemoveAll(m => m.Source == side && m.Kind == item.Kind && m.SourceId == item.SourceId
            && (m.DestinationId == destinationId || m.DestinationId == item.ExpectedDecision?.DestinationId && m.Decision is "skip" or "destinationRemoved"));
        mappings.Add(new(item.Kind, item.SourceId, destinationId, side, decision));
    }
    private void AddCredits()
    {
        var applicable = source.Own ? source.Trip.Credits : TripCreditCodec.Inherited(new TripCreditCodec.Attribution(1,
            source.Trip.Name, source.Trip.ContributionPolicy, source.Trip.Credits));
        var credits = destination.Credits.Concat(applicable).Distinct().ToArray();
        try { TripCreditCodec.Validate(credits); }
        catch (FormatException) { throw Invalid("Required notices exceed supported credit limits. Review and consolidate the readable notices without erasing still-applicable terms before copying."); }
        destination.Credits = credits;
    }
    private static TripMetadataMutationRejected Invalid(string detail) => TripCompositionOrdering.Invalid(detail);
    private sealed record Block(List<Guid> Ids, TripCompositionPlacement? Placement);
}
