using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Atomic owned Version and durable A/B comparison lifecycle. Source content is never applied again on resume.</summary>
public sealed partial class TripCompositionService(ApplicationDbContext db)
{
    /// <summary>Creates one independent private version or comparison; repeated requests are deliberate separate creations.</summary>
    public async Task<TripConstructionResult> CreateAsync(string owner, Guid sourceA, Guid? sourceB,
        string? name, string? changeNote, bool keepPlanning = true,
        Func<CancellationToken, Task<bool>>? credential = null, CancellationToken token = default)
    {
        if (name != null && (string.IsNullOrWhiteSpace(name) || name.Length > 200) || changeNote?.Length > 2000)
            throw new TripMetadataMutationRejected(400, "construction-invalid", "Supply a name of 1–200 characters and a change note of at most 2000 characters.");
        var ids = new[] { sourceA, sourceB }.OfType<Guid>().Distinct().Order().ToArray();
        var owners = await db.Trips.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => t.UserId).ToArrayAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var committing = false;
        try
        {
            foreach (var user in owners.Append(owner).Distinct().Order(StringComparer.Ordinal))
                await LocationDeletionFence.AcquireAsync(db, user, token);
            foreach (var user in owners.Append(owner).Distinct().Order(StringComparer.Ordinal))
                await RequireActiveAsync(user, token);
            var profiles = await db.Segments.AsNoTracking().Where(s => ids.Contains(s.TripId) && s.TransportProfileId != null)
                .Select(s => s.TransportProfileId!.Value).Distinct().OrderBy(id => id).ToArrayAsync(token);
            await SegmentRouteReconciler.LockProfilesAsync(db, profiles, token);
            foreach (var id in ids)
                await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {id} FOR SHARE").AsNoTracking().ToListAsync(token);
            if (credential != null && !await credential(token)) throw Unavailable();
            var construction = new TripCloneCoordinator(db);
            var a = await construction.LoadSourceAsync(sourceA, token);
            if (a == null || a.UserId != owner) throw Unavailable();
            var b = sourceB.HasValue ? await construction.LoadSourceAsync(sourceB.Value, token) : null;
            if (sourceB.HasValue && (b == null || b.UserId != owner && (!b.IsPublic || !TripReusePolicy.GrantsReuse(b)))) throw Unavailable();
            // Ownership discovered before the transaction is only lock ordering input, never authority.
            if (!owners.Contains(a.UserId) || b != null && !owners.Contains(b.UserId)) throw Unavailable();
            var mappings = new List<TripCompositionMapping>();
            Trip c;
            try { c = await construction.ConstructCloneAsync(a, owner, token, keepPlanning, mappings); }
            catch (Exception invalid) when (invalid is InvalidOperationException or FormatException)
            {
                throw new TripMetadataMutationRejected(400, "construction-source-invalid", "The source graph cannot be copied unchanged. Review its saved references, route compatibility and portable fields before trying again.");
            }
            c.Name = name?.Trim() ?? a.Name;
            TripRichNotes.NormalizeDestination(c);
            var compositionId = sourceB.HasValue ? Guid.NewGuid() : (Guid?)null;
            var mediaOmitted = false;
            if (b != null)
            {
                if (b.UserId != owner) mediaOmitted = TripCopyMedia.OmitUnqualified(b);
                db.Set<TripComposition>().Add(new()
                {
                    Id = compositionId!.Value, TripId = c.Id, UserId = owner, CreatedAt = DateTime.UtcNow,
                    SourceAId = a.Id, SourceBId = b.Id,
                    SnapshotA = TripCompositionSnapshot.Capture(a, true),
                    SnapshotB = TripCompositionSnapshot.Capture(b, b.UserId == owner, mediaOmitted),
                    Mappings = JsonSerializer.Serialize(mappings, TripCompositionSnapshot.Json)
                });
            }
            db.Set<TripOrigin>().Add(new()
            {
                Id = Guid.NewGuid(), TripId = c.Id, SourceTripId = a.Id, CreatedAt = DateTime.UtcNow, ChangeNote = changeNote
            });
            await construction.PersistConstructedAsync(c, token);
            var saved = await construction.LoadSourceAsync(c.Id, token) ?? throw Unavailable();
            var current = TripCompositionSnapshot.Project(saved, true);
            committing = true;
            await transaction.CommitAsync(token);
            return new(c.Id, compositionId, compositionId.HasValue ? "inProgress" : "created", mediaOmitted, current);
        }
        catch (Exception primary)
        {
            db.ChangeTracker.Clear();
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception cleanup) { throw new AggregateException("Construction failed and rollback could not be confirmed.", primary, cleanup); }
            if (!committing && primary.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                throw new TripMetadataMutationRejected(409, "construction-contention", "Source authority or content changed. Refresh before starting a new attempt.");
            throw;
        }
    }

    /// <summary>Lists only unfinished destinations owned by the active caller, optionally for the exact ordered source pair.</summary>
    public async Task<object> ListAsync(string owner, Guid? sourceA, Guid? sourceB, CancellationToken token = default,
        Func<CancellationToken, Task<bool>>? credential = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await RequireActiveAsync(owner, token);
        if (credential != null && !await credential(token)) throw Unavailable();
        return await (from workspace in db.Set<TripComposition>().AsNoTracking()
            join trip in db.Trips.AsNoTracking() on workspace.TripId equals trip.Id
            where trip.UserId == owner && workspace.FinishedAt == null
                && (!sourceA.HasValue || workspace.SourceAId == sourceA) && (!sourceB.HasValue || workspace.SourceBId == sourceB)
            orderby workspace.CreatedAt descending
            select new { compositionId = workspace.Id, tripId = trip.Id, trip.Name, workspace.CreatedAt }).ToArrayAsync(token);
    }

    /// <summary>Reads immutable inputs and current C coherently. Missing mapped identities are removed, never reconstructed.</summary>
    public async Task<object> ReadAsync(string owner, Guid id, CancellationToken token = default,
        Func<CancellationToken, Task<bool>>? credential = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await LocationDeletionFence.AcquireAsync(db, owner, token);
        await TripCompositionLifecycle.LockAsync(db, owner, null, id, token);
        var workspace = await OwnedAsync(owner, id, token);
        if (workspace.FinishedAt != null) throw Closed();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Trips\" WHERE \"Id\" = {workspace.TripId} FOR SHARE", token);
        await RequireActiveAsync(owner, token);
        if (credential != null && !await credential(token)) throw Unavailable();
        var current = await new TripCloneCoordinator(db).LoadSourceAsync(workspace.TripId, token) ?? throw Unavailable();
        var mappings = JsonSerializer.Deserialize<List<TripCompositionMapping>>(workspace.Mappings!, TripCompositionSnapshot.Json)!;
        // JSONB normalizes text formatting; persist only an actual decision change.
        if (MarkRemoved(mappings, current))
        {
            var decisions = JsonSerializer.Serialize(mappings, TripCompositionSnapshot.Json);
            await db.Set<TripComposition>().Where(w => w.Id == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.Mappings, decisions), token);
        }
        await transaction.CommitAsync(token);
        return new
        {
            compositionId = workspace.Id, tripId = current.Id, status = "inProgress",
            sourceA = JsonSerializer.Deserialize<JsonElement>(workspace.SnapshotA!),
            sourceB = JsonSerializer.Deserialize<JsonElement>(workspace.SnapshotB!),
            mappings = mappings.Select(m => new { source = m.Source, kind = m.Kind, sourceId = m.SourceId, destinationId = m.DestinationId, decision = m.Decision }).ToArray(),
            current = TripCompositionSnapshot.Project(current, true)
        };
    }

    /// <summary>Closes comparison under a destination row barrier; changes no Trip field or public freshness.</summary>
    public async Task<TripConstructionResult> FinishAsync(string owner, Guid id,
        Func<CancellationToken, Task<bool>>? credential = null, CancellationToken token = default)
    {
        await using var transaction = await LocationDeletionFence.BeginAsync(db, owner, token);
        var committing = false;
        try
        {
            await RequireActiveAsync(owner, token);
            await TripCompositionLifecycle.LockAsync(db, owner, null, id, token);
            var workspace = await OwnedAsync(owner, id, token);
            var trips = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {workspace.TripId} FOR UPDATE").AsNoTracking().ToListAsync(token);
            if (trips.Count != 1 || trips[0].UserId != owner || credential != null && !await credential(token)) throw Unavailable();
            // Reload after the destination wait: another Finish or deletion may have won.
            workspace = await OwnedAsync(owner, id, token);
            if (workspace.FinishedAt != null) throw Closed();
            await db.Set<TripComposition>().Where(w => w.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.FinishedAt, DateTime.UtcNow).SetProperty(w => w.SnapshotA, (string?)null)
                .SetProperty(w => w.SnapshotB, (string?)null).SetProperty(w => w.Mappings, (string?)null)
                .SetProperty(w => w.SourceAId, (Guid?)null).SetProperty(w => w.SourceBId, (Guid?)null), token);
            committing = true;
            await transaction!.CommitAsync(token);
            return new(workspace.TripId, workspace.Id, "finished", false);
        }
        catch (Exception primary)
        {
            try { await transaction!.RollbackAsync(CancellationToken.None); }
            catch (Exception cleanup) { throw new AggregateException("Finish failed and rollback could not be confirmed.", primary, cleanup); }
            if (!committing && primary.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                throw new TripMetadataMutationRejected(409, "composition-contention", "The comparison changed while waiting. Refresh its current state.");
            throw;
        }
    }

    private async Task RequireActiveAsync(string owner, CancellationToken token)
    {
        var users = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {owner} AND \"IsActive\" FOR SHARE").AsNoTracking().ToListAsync(token);
        if (users.Count != 1) throw Unavailable();
    }

    private async Task<TripComposition> OwnedAsync(string owner, Guid id, CancellationToken token) =>
        await (from w in db.Set<TripComposition>().AsNoTracking() join t in db.Trips on w.TripId equals t.Id
            where w.Id == id && t.UserId == owner select w).SingleOrDefaultAsync(token) ?? throw Unavailable();

    private static TripMetadataMutationRejected Unavailable() => new(404, "composition-unavailable", "The requested Trip or composition is unavailable.");
    private static TripMetadataMutationRejected Closed() => new(409, "composition-closed", "This comparison is finished and cannot reopen.");
}

/// <summary>Confirmed ordinary creation/lifecycle response; no request receipt or retry promise.</summary>
public sealed record TripConstructionResult(Guid TripId, Guid? CompositionId, string Status, bool MediaOmitted, object? Current = null);
