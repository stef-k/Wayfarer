using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Util;
using static Wayfarer.Services.LocationMutations.LocationMutationReceipts;

namespace Wayfarer.Services.LocationMutations;

/// <summary>Property-scoped effects always use the final locked row and share the terminal receipt transaction.</summary>
internal sealed partial class LocationMutationService
{
    /// <summary>Validates catalog identity before effects; the optional provider never owns coordinate completion.</summary>
    private async Task<LocationMutationReply> ApplyAndCommitAsync(IDbContextTransaction? transaction,
        LocationMutationOperation operation, LocationMutationCommand command, ReverseGeocodingResult? provider, CancellationToken token)
    {
        var now = await NowAsync(db, token);
        if (now >= operation.ExecuteBeforeUtc)
            return await CommitAsync(transaction, Terminal(operation, operation.ExecuteBeforeUtc, "operationExecutionExpired", 409), token);
        if (command.Kind == "delete")
        {
            await LocationDeletionFence.DeleteInTransactionAsync(db, operation.UserId, [operation.LocationId], cache, token);
            return await CommitAsync(transaction, Terminal(operation, now, changed: true), token);
        }
        var changes = command.Changes;
        int? activityId = null;
        if (changes.TryGetProperty("activity", out var activity) && activity.GetProperty("op").GetString() == "set")
        {
            activityId = await new ActivityCatalog(db).ResolveIdAsync(activity.GetProperty("id").GetInt32(), token);
            if (activityId == null) return await CommitAsync(transaction, Terminal(operation, now, "activityInvalid", 422), token);
            // FK key-share prevents a catalog retirement racing the final assignment.
            await db.Database.SqlQuery<int>($$"""
                SELECT 1 AS "Value" FROM "ActivityTypes" WHERE "Id" = {{activityId.Value}} FOR KEY SHARE
                """).ToListAsync(token);
            if (!await db.ActivityTypes.AnyAsync(row => row.Id == activityId, token))
                return await CommitAsync(transaction, Terminal(operation, now, "activityInvalid", 422), token);
        }
        var before = await ValuesAsync(operation.UserId, operation.LocationId, token);
        var current = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == operation.UserId && row.Id == operation.LocationId, token);
        db.Attach(current);
        var moved = Movement(command, before);
        var addressOutcome = "unchanged";
        var providerOutcome = "notRequested";
        if (moved)
        {
            var position = changes.GetProperty("position");
            current.Coordinates = new(position.GetProperty("longitude").GetDouble(), position.GetProperty("latitude").GetDouble()) { SRID = 4326 };
            ClearAddress(current);
            addressOutcome = "cleared"; providerOutcome = "unavailable";
            if (provider is { Succeeded: true, Authority: not null } && await reverse.IsAuthorityCurrentAsync(provider.Authority, token))
            { provider.ApplyTo(current, DateTimeOffset.UtcNow); addressOutcome = "replaced"; providerOutcome = "succeeded"; }
        }
        var timeChanged = false;
        if (changes.TryGetProperty("eventUtc", out var time))
        {
            var instant = LocationMutationCommand.Instant(time.GetProperty("value").GetString()!, false);
            timeChanged = current.LocalTimestamp != instant;
            current.LocalTimestamp = instant;
        }
        if (moved || timeChanged)
        {
            LocationPresence.Invalidate(current, db);
            try { current.TimeZoneId = CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(current.Coordinates.Y, current.Coordinates.X); }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
            { /* History derivation retains an explicit unresolved result without changing the capture instant. */ }
            cache.Remove($"lastLocation_{operation.UserId}");
        }
        if (changes.TryGetProperty("notes", out var notes)) current.Notes = notes.GetProperty("op").GetString() == "clear" ? null : RichNotes.Normalize(notes.GetProperty("html").GetString());
        if (changes.TryGetProperty("activity", out _)) current.ActivityTypeId = activityId;
        if (changes.TryGetProperty("altitude", out var altitude)) current.Altitude = altitude.GetProperty("op").GetString() == "clear" ? null : altitude.GetProperty("value").GetDouble();
        if (changes.TryGetProperty("manualAddress", out var manual))
        {
            foreach (var field in manual.GetProperty("fields").EnumerateObject())
            {
                var property = char.ToUpperInvariant(field.Name[0]) + field.Name[1..];
                var value = field.Value.ValueKind == JsonValueKind.Null ? null : field.Value.GetString()?.Trim();
                db.Entry(current).Property(property).CurrentValue = string.IsNullOrWhiteSpace(value) ? null : value;
            }
            ClearProvenance(current); addressOutcome = "manual";
        }
        if (changes.TryGetProperty("position", out _) || changes.TryGetProperty("manualAddress", out _))
        {
            current.OriginalCreateAddressToken = null;

        }
        await db.Database.SqlQuery<int>($$"""
            SELECT 1 AS "Value" FROM "LocationEnrichmentAttempts" WHERE "UserId" = {{operation.UserId}} AND "LocationId" = {{operation.LocationId}} FOR UPDATE
            """).ToListAsync(token);
        // Check after the last lock wait and before any content/receipt persistence.
        now = await NowAsync(db, token);
        if (now >= operation.ExecuteBeforeUtc)
        {
            db.Entry(current).State = EntityState.Detached;
            return await CommitAsync(transaction, Terminal(operation, operation.ExecuteBeforeUtc, "operationExecutionExpired", 409), token);
        }
        if (changes.TryGetProperty("position", out _) || changes.TryGetProperty("manualAddress", out _))
            await InvalidateBackfillAsync(operation.UserId, operation.LocationId, token);
        await db.SaveChangesAsync(token);
        var after = await ValuesAsync(operation.UserId, operation.LocationId, token);
        var changed = !LocationMutationCommand.Equal("content", before!.Value.GetProperty("deleteContent").GetProperty("value"), after!.Value.GetProperty("deleteContent").GetProperty("value"));
        return await CommitAsync(transaction, Terminal(operation, now, changed: changed, address: addressOutcome, provider: providerOutcome), token);
    }

    /// <summary>Preserves consumed attempts/accounting while excluding superseded backfill publications.</summary>
    private Task<int> InvalidateBackfillAsync(string owner, int id, CancellationToken token) => db.LocationEnrichmentAttempts
        .Where(attempt => attempt.UserId == owner && attempt.LocationId == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(attempt => attempt.OperationId, (Guid?)null)
            .SetProperty(attempt => attempt.OperationLeaseId, (Guid?)null)
            .SetProperty(attempt => attempt.OperationFencingGeneration, (long?)null)
            .SetProperty(attempt => attempt.OperationStartedAtUtc, (DateTime?)null)
            .SetProperty(attempt => attempt.OperationWorkflowEpoch, (int?)null)
            .SetProperty(attempt => attempt.OperationAttemptNumber, (int?)null), token);

    /// <summary>Movement makes the entire old tuple obsolete before any replacement/manual overlay.</summary>
    private static void ClearAddress(Location row)
    {
        row.Address = null; row.FullAddress = null; row.AddressNumber = null; row.StreetName = null;
        row.PostCode = null; row.Place = null; row.Region = null; row.Country = null;
        ClearProvenance(row);
    }

    /// <summary>Mixed/manual content cannot retain a pure provider attribution or provider-only feature display.</summary>
    private static void ClearProvenance(Location row)
    {
        row.ProviderAddressLine1 = null; row.ResolvedFeatureName = null; row.ResolvedFeatureType = null;
        row.ReverseGeocodingProvider = null; row.ReverseGeocodingStorageMode = null; row.ReverseGeocodedAt = null;
    }
}
