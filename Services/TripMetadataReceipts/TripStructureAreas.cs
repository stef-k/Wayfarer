using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Util;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Serializes Area membership writes on the same child, Region and Trip lock classes as lifecycle deletion.</summary>
internal sealed class TripStructureAreas(ApplicationDbContext db, IDataProtectionProvider protection, TimeProvider time)
{
    private const string Purpose = "Wayfarer.TripStructure.AreaDeletion.v1";

    /// <summary>Creates one Area against exact sibling membership and nullable stored orders.</summary>
    internal async Task<TripStructureResult> CreateAsync(TripStructureWrite write)
    {
        var root = PlaceStructuralRequest.Fields(write.Payload, "expected", "value");
        var originals = PlaceStructuralRequest.Fields(root.GetValueOrDefault("expected"), "areas");
        var expected = ReadVector(originals.GetValueOrDefault("areas"), material: false);
        var value = root.GetValueOrDefault("value");
        var fields = PlaceStructuralRequest.Fields(value, "name", "notesHtml", "fillHex", "geometry");
        _ = PlaceStructuralRequest.Fields(fields.GetValueOrDefault("geometry"), "type", "coordinates");
        if (fields.Count != 4 || !EditorAreaRequestParser.TryParseCreate(value, out var parsed, out _))
            throw TripStructureBodies.Invalid();
        var trip = await LockParentAsync(write, write.TargetId, creating: true);
        var areas = await ReadAreasAsync(write.TargetId, write.Token);
        if (!PlaceStructuralRequest.Equal(expected, Orders(areas))) throw Stale();
        var maximum = areas.Select(area => area.DisplayOrder ?? 0).DefaultIfEmpty().Max();
        if (maximum == int.MaxValue) throw TripStructureBodies.Invalid();
        var created = new Area
        {
            Id = Guid.NewGuid(), RegionId = write.TargetId, Name = parsed.Name, Notes = parsed.NotesHtml ?? string.Empty,
            FillHex = parsed.FillHex, Geometry = parsed.Geometry, DisplayOrder = maximum + 1
        };
        db.Areas.Add(created);
        await db.SaveChangesAsync(write.Token);
        await StampAsync(trip, write.Token);
        var saved = await ReadAreasAsync(write.TargetId, write.Token);
        string[] fieldsToDigest = ["name", "notes", "fillHex", "geometry"];
        return new(true, new
        {
            created.Id, created.RegionId, created.DisplayOrder,
            fieldDigests = fieldsToDigest.ToDictionary(key => key, key => TripStructureDigests.Area(created, key)),
            areaOrder = Vector(created.RegionId, saved)
        });
    }

    /// <summary>Rewrites only the complete reviewed scope and captures the stored vector.</summary>
    internal async Task<TripStructureResult> OrderAsync(TripStructureWrite write)
    {
        var root = PlaceStructuralRequest.Fields(write.Payload, "expected", "areaIds");
        var originals = PlaceStructuralRequest.Fields(root.GetValueOrDefault("expected"), "areas");
        var expected = ReadVector(originals.GetValueOrDefault("areas"), material: true);
        var vector = root.GetValueOrDefault("areaIds");
        if (vector.ValueKind != JsonValueKind.Array) throw TripStructureBodies.Invalid();
        var order = vector.EnumerateArray().Select(ReadId).ToArray();
        if (!order.Order().SequenceEqual(expected.EnumerateArray().Select(row => ReadId(row.GetProperty("id")))))
            throw TripStructureBodies.Invalid();

        var (trip, areas) = await LockScopeAsync(write, write.TargetId);
        if (!PlaceStructuralRequest.Equal(expected, areas.OrderBy(area => area.Id).Select(Material).ToArray())) throw Stale();
        var changed = await SetOrderAsync(order.Select(id => areas.Single(area => area.Id == id)), write.Token);
        if (changed) await StampAsync(trip, write.Token);
        return new(changed, new { areaOrder = Vector(write.TargetId, await ReadAreasAsync(write.TargetId, write.Token)) });
    }

    /// <summary>Signs the original Area and exact sibling order from one owned preview snapshot.</summary>
    internal async Task<object> PreviewAsync(TripStructureWrite write)
    {
        var root = PlaceStructuralRequest.Fields(write.Payload, "expected");
        var originals = PlaceStructuralRequest.Fields(root.GetValueOrDefault("expected"), "area", "areas");
        var order = ReadVector(originals.GetValueOrDefault("areas"), material: false);
        var material = originals.GetValueOrDefault("area");
        ValidateMaterial(material);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, write.Token);
        var area = await FindTargetAsync(write);
        var areas = await ReadAreasAsync(area.RegionId, write.Token);
        if (!PlaceStructuralRequest.Equal(material, Material(area)) || !PlaceStructuralRequest.Equal(order, Orders(areas)))
            throw Stale();
        var expiresAt = time.GetUtcNow().AddMinutes(10);
        var approval = Approval(write, area, areas, expiresAt);
        var token = protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(approval));
        return new
        {
            contract = TripMetadataIdentity.StructureVersion, write.TripId, areaId = area.Id, area.RegionId, area.Name,
            hasPlanning = area.Planning != null, confirmation = new { token, expiresAt }
        };
    }

    /// <summary>Rechecks consent against stored material and order before removing the Area and normalizing its siblings.</summary>
    internal async Task<TripStructureResult> DeleteAsync(TripStructureWrite write)
    {
        var root = PlaceStructuralRequest.Fields(write.Payload, "confirmationToken");
        var token = root.GetValueOrDefault("confirmationToken");
        if (token.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || token.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(token.GetString()))
            throw new TripMetadataMutationRejected(409, "mutation-review-required", "Review the Area deletion before applying.");
        if (token.ValueKind != JsonValueKind.String) throw TripStructureBodies.Invalid();
        var approval = ReadApproval(token.GetString()!);

        var candidate = await FindTargetAsync(write);
        var (trip, areas) = await LockScopeAsync(write, candidate.RegionId);
        var area = areas.SingleOrDefault(item => item.Id == write.TargetId) ?? throw Stale();
        if (approval.ExpiresAt <= time.GetUtcNow() || approval != Approval(write, area, areas, approval.ExpiresAt))
            throw new TripMetadataMutationRejected(409, "area-deletion-preview-stale", "Review a fresh Area deletion preview.");
        var planningDigest = TripStructureDigests.Planning(area.Planning);
        await db.Areas.Where(item => item.Id == area.Id).ExecuteDeleteAsync(write.Token);
        await SetOrderAsync(areas.Where(item => item.Id != area.Id), write.Token);
        await StampAsync(trip, write.Token);
        return new(true, new
        {
            area.Id, area.RegionId, planningDigest, areaOrder = Vector(area.RegionId, await ReadAreasAsync(area.RegionId, write.Token))
        });
    }

    /// <summary>Finds an Area through the owned Region and Trip chain.</summary>
    private async Task<Area> FindTargetAsync(TripStructureWrite write) =>
        await db.Areas.AsNoTracking().SingleOrDefaultAsync(area => area.Id == write.TargetId && area.Region.TripId == write.TripId
            && area.Region.UserId == write.Subject && area.Region.Trip.UserId == write.Subject, write.Token) ?? throw Missing();

    /// <summary>Rechecks complete membership after sorted Area locks and the parent barrier.</summary>
    private async Task<(Trip Trip, Area[] Areas)> LockScopeAsync(TripStructureWrite write, Guid regionId)
    {
        var candidates = await db.Areas.Where(area => area.RegionId == regionId).OrderBy(area => area.Id)
            .Select(area => area.Id).ToArrayAsync(write.Token);
        foreach (var id in candidates)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Areas\" WHERE \"Id\" = {id} FOR UPDATE", write.Token);
        var trip = await LockParentAsync(write, regionId);
        var areas = await ReadAreasAsync(regionId, write.Token);
        if (!areas.Select(area => area.Id).Order().SequenceEqual(candidates)) throw Stale();
        return (trip, areas);
    }

    /// <summary>Checks final ownership and admission after the Region and Trip waits.</summary>
    private async Task<Trip> LockParentAsync(TripStructureWrite write, Guid regionId, bool creating = false)
    {
        var region = await db.Regions.FromSqlInterpolated($"""
            SELECT * FROM public."Regions" WHERE "Id" = {regionId} AND "TripId" = {write.TripId}
                AND "UserId" = {write.Subject} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(write.Token) ?? throw Missing();
        var trip = await db.Trips.FromSqlInterpolated($"""
            SELECT * FROM public."Trips" WHERE "Id" = {write.TripId} AND "UserId" = {write.Subject} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(write.Token) ?? throw Missing();
        await write.Readmit(write.Token);
        if (creating && region.Name == "Unassigned Places")
            throw new TripMetadataMutationRejected(403, "mutation-operation-forbidden", "Areas cannot be created in Unassigned Places.");
        return trip;
    }

    /// <summary>Loads canonical order using the database's configured name collation.</summary>
    private Task<Area[]> ReadAreasAsync(Guid regionId, CancellationToken token) =>
        db.Areas.AsNoTracking().Where(area => area.RegionId == regionId).OrderBy(area => area.DisplayOrder == null)
            .ThenBy(area => area.DisplayOrder).ThenBy(area => area.Name).ThenBy(area => area.Id).ToArrayAsync(token);

    /// <summary>Writes changed orders only, including a null-to-numeric change.</summary>
    private async Task<bool> SetOrderAsync(IEnumerable<Area> ordered, CancellationToken token)
    {
        var position = 0;
        var changed = false;
        foreach (var area in ordered)
        {
            position++;
            if (area.DisplayOrder == position) continue;
            await db.Areas.Where(item => item.Id == area.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.DisplayOrder, position), token);
            changed = true;
        }
        return changed;
    }

    /// <summary>Keeps saved Trip freshness monotonic across the explicit and EF-owned timestamp updates.</summary>
    private Task<int> StampAsync(Trip trip, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var stamp = trip.UpdatedAt > now ? trip.UpdatedAt : now;
        return db.Trips.Where(item => item.Id == trip.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt > stamp ? item.UpdatedAt : stamp), token);
    }

    /// <summary>Canonicalizes unordered originals by identity while retaining every exact nullable value.</summary>
    private static JsonElement ReadVector(JsonElement rows, bool material)
    {
        if (rows.ValueKind != JsonValueKind.Array) throw TripStructureBodies.Invalid();
        var values = new Dictionary<Guid, JsonElement>();
        foreach (var row in rows.EnumerateArray())
        {
            if (material) ValidateMaterial(row);
            else if (PlaceStructuralRequest.Fields(row, "id", "displayOrder").Count != 2) throw TripStructureBodies.Invalid();
            var order = row.GetProperty("displayOrder");
            if (order.ValueKind != JsonValueKind.Null && (order.ValueKind != JsonValueKind.Number || !order.TryGetInt32(out _)))
                throw TripStructureBodies.Invalid();
            if (!values.TryAdd(ReadId(row.GetProperty("id")), row)) throw TripStructureBodies.Invalid();
        }
        return JsonSerializer.SerializeToElement(values.OrderBy(pair => pair.Key).Select(pair => pair.Value));
    }

    /// <summary>Requires all material comparison members before comparing against the saved values.</summary>
    private static void ValidateMaterial(JsonElement row)
    {
        var fields = PlaceStructuralRequest.Fields(row, "id", "regionId", "displayOrder", "name", "notes", "fillHex", "geometry", "planning");
        if (fields.Count != 8) throw TripStructureBodies.Invalid();
        _ = ReadId(fields["id"]);
        _ = ReadId(fields["regionId"]);
    }

    /// <summary>Accepts only the canonical nonzero ID in protected originals.</summary>
    private static Guid ReadId(JsonElement value) => value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id)
        && id != Guid.Empty && value.GetString() == id.ToString("D") ? id : throw TripStructureBodies.Invalid();

    /// <summary>Builds the proof material from the same published comparisons available to the owner.</summary>
    private static object Material(Area area) => new
    {
        area.Id, area.RegionId, area.DisplayOrder, area.Name, notes = RichNotes.Normalize(area.Notes), area.FillHex,
        geometry = AreaGeometryIdentity.For(area.Geometry), area.Planning
    };

    /// <summary>Projects nullable stored Area position for both original and result vectors.</summary>
    private static object Item(Area area) => new { area.Id, area.DisplayOrder };

    /// <summary>Builds an unordered set for comparisons, in stable identity order.</summary>
    private static object[] Orders(IEnumerable<Area> areas) => areas.OrderBy(area => area.Id).Select(Item).ToArray();

    /// <summary>Returns the committed visible order while preserving each nullable value.</summary>
    private static object Vector(Guid regionId, IEnumerable<Area> areas) => new { regionId, items = areas.Select(Item).ToArray() };

    /// <summary>Binds both exact original comparisons, scope and expiry to this approval.</summary>
    private static AreaApproval Approval(TripStructureWrite write, Area area, Area[] areas, DateTimeOffset expiresAt) =>
        new(write.Subject, write.Web, write.TripId, area.Id, area.RegionId,
            PlaceStructuralRequest.Hash(new { area = Material(area), areas = Orders(areas) }), expiresAt);

    /// <summary>Refuses malformed or differently purposed confirmation material.</summary>
    private AreaApproval ReadApproval(string token)
    {
        try
        {
            return JsonSerializer.Deserialize<AreaApproval>(protection.CreateProtector(Purpose).Unprotect(token)) ?? throw new JsonException();
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new TripMetadataMutationRejected(409, "area-deletion-preview-stale", "Review a fresh Area deletion preview.");
        }
    }

    private sealed record AreaApproval(string Subject, bool Web, Guid TripId, Guid AreaId, Guid RegionId, string Fingerprint, DateTimeOffset ExpiresAt);

    /// <summary>Refuses changed Area material or membership without exposing saved values.</summary>
    private static TripMetadataMutationRejected Stale() => new(409, "mutation-base-stale", "Area material or order changed. Review the current values.");

    /// <summary>Conceals missing or foreign scope.</summary>
    private static TripMetadataMutationRejected Missing() => new(404, "mutation-scope-unavailable", "Area edit scope not found.");
}
