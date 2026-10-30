using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Parsers;
using static Wayfarer.Services.LocationMutations.LocationMutationReceipts;

namespace Wayfarer.Services.LocationMutations;

/// <summary>Owns account-fenced command binding, selected-original comparison and atomic effect/receipt commits.</summary>
internal sealed partial class LocationMutationService(ApplicationDbContext db, IDataProtectionProvider protection,
    ReverseGeocodingService reverse, IMemoryCache cache)
{
    /// <summary>Current request credentials must be rechecked inside every account-fenced writer/admission transaction.</summary>
    internal required Func<CancellationToken, Task<bool>> Authorized { get; init; }

    /// <summary>Only the first valid invocation may execute; duplicates recover before current target validation.</summary>
    internal async Task<LocationMutationReply> ExecuteAsync(string owner, int id, Guid baseId, Guid actionId,
        LocationMutationCommand command, byte[] bytes, CancellationToken token)
    {
        var fingerprint = LocationMutationCommand.Fingerprint(owner, id, baseId, actionId, command.Adapter, bytes);
        LocationMutationOperation operation;
        await using (var transaction = await LocationDeletionFence.BeginAsync(db, owner, token))
        {
            if (!await Authorized(token)) return Problem("mutationForbidden", 403);
            var existing = await FindAsync(owner, baseId, actionId, token);
            var now = await NowAsync(db, token);
            if (existing != null)
                return existing.LocationId != id || existing.Fingerprint != fingerprint
                    ? Problem("operationIdentityReused", 409) : Observe(existing, now);
            var proof = LocationMutationBase.Read(protection, command.Handle, owner, id, baseId);
            if (proof == null) return Problem("mutationInvalid", 422);
            if (now >= proof.ExpiresAtUtc) return Problem("operationExpired", 410);
            operation = new LocationMutationOperation
            {
                UserId = owner, LocationId = id, Uuid = proof.Uuid, BaseId = baseId, ActionId = actionId,
                Adapter = command.Adapter, Kind = command.Kind, Normalization = command.Normalization, Fingerprint = fingerprint,
                ExpiresAtUtc = proof.ExpiresAtUtc, AcceptedAtUtc = now, ExecuteBeforeUtc = proof.ExpiresAtUtc,
                ReceiptRetainUntilUtc = proof.ExpiresAtUtc.AddDays(30), State = "pending", ExecutorNonce = Guid.NewGuid()
            };
            db.LocationMutationOperations.Add(operation);
            var rejection = command.Normalization != "location-values-1" || proof.Normalization != command.Normalization
                ? Terminal(operation, now, "baseNormalizationUnsupported", 422) : await CompareAsync(operation, command, token);
            if (rejection != null) return await CommitAsync(transaction, rejection, token);
            if (!Movement(command, await ValuesAsync(owner, id, token)))
                return await ApplyAndCommitAsync(transaction, operation, command, null, token);
            operation.ExecuteBeforeUtc = new[] { now.AddSeconds(120), proof.ExpiresAtUtc }.Min();
            await db.SaveChangesAsync(token);
            if (transaction != null) await transaction.CommitAsync(token);
        }
        var nonce = operation.ExecutorNonce;
        ReverseGeocodingResult? result = null;
        // Optional provider timeout is independent of the final valid-coordinate transaction.
        using (var providerBudget = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            providerBudget.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var position = command.Changes.GetProperty("position");
                var draft = new Location { Id = id, UserId = owner, IdempotencyKey = operation.Uuid, TimeZoneId = "UTC",
                    Coordinates = new(position.GetProperty("longitude").GetDouble(), position.GetProperty("latitude").GetDouble()) { SRID = 4326 } };
                result = await reverse.EnrichRetainedAsync(db, draft, providerBudget.Token, beforeAdmission: async admissionToken =>
                {
                    if (!await Authorized(admissionToken)) throw new MutationProtocolException("mutationForbidden");
                    var reserved = await FindAsync(owner, baseId, actionId, admissionToken);
                    if (reserved == null || reserved.ExecutorNonce != nonce || reserved.State != "pending"
                        || await NowAsync(db, admissionToken) >= reserved.ExecuteBeforeUtc)
                        throw new MutationProtocolException("operationExecutionExpired");
                }, beforeContact: async contactToken =>
                {
                    // The operation lock was acquired before provider locks; compare the Location only after them.
                    var reserved = db.ChangeTracker.Entries<LocationMutationOperation>().Single(entry => entry.Entity.BaseId == baseId && entry.Entity.ActionId == actionId).Entity;
                    if (await CompareAsync(reserved, command, contactToken) != null)
                        throw new MutationProtocolException("locationBaseStale");
                });
            }
            catch (Exception failure) when (failure is OperationCanceledException or HttpRequestException or MutationProtocolException or LocationDeletedException or NpgsqlException or DbUpdateException or IOException)
            { /* Optional failure cannot cancel a still-valid coordinate command. Final locks decide publication. */ }
        }
        db.ChangeTracker.Clear();
        await using var finalTransaction = await LocationDeletionFence.BeginAsync(db, owner, token);
        if (!await Authorized(token)) return Problem("mutationForbidden", 403);
        var finalOperation = await FindAsync(owner, baseId, actionId, token);
        if (finalOperation == null) return Problem("mutationOutcomeUnknown", 503);
        if (finalOperation.State != "pending" || finalOperation.ExecutorNonce != nonce)
            return Observe(finalOperation, await NowAsync(db, token));
        await LockProviderAsync(result, owner, token);
        var finalRejection = await CompareAsync(finalOperation, command, token);
        if (finalRejection != null) return await CommitAsync(finalTransaction, finalRejection, token);
        return await ApplyAndCommitAsync(finalTransaction, finalOperation, command, result, token);
    }

    /// <summary>Recovery is synchronized and read-only; absence before expiry still permits a delayed original POST.</summary>
    internal async Task<LocationMutationReply> RecoverAsync(string owner, int id, Guid baseId, Guid actionId, string? handle, CancellationToken token)
    {
        await using var transaction = await LocationDeletionFence.BeginAsync(db, owner, token);
        if (!await Authorized(token)) return Problem("mutationForbidden", 403);
        var operation = await FindAsync(owner, baseId, actionId, token);
        var now = await NowAsync(db, token);
        if (operation != null && operation.LocationId == id) return Observe(operation, now);
        if (operation != null) return Problem("operationIdentityReused", 409);
        return Unknown(owner, id, baseId, actionId, now, LocationMutationBase.Read(protection, handle, owner, id, baseId));
    }

    /// <summary>Checks the exact selected union after row locks without materializing damaged content for delete.</summary>
    private async Task<LocationMutationReply?> CompareAsync(LocationMutationOperation operation, LocationMutationCommand command, CancellationToken token)
    {
        var deleted = await LocationDeletionFence.IsDeletedAsync(db, operation.UserId, operation.Uuid, token);
        await db.Database.SqlQuery<int>($$"""
            SELECT 1 AS "Value" FROM "Locations" WHERE "UserId" = {{operation.UserId}} AND "Id" = {{operation.LocationId}} FOR UPDATE
            """).ToListAsync(token);
        var values = await ValuesAsync(operation.UserId, operation.LocationId, token);
        var now = await NowAsync(db, token);
        if (now >= operation.ExecuteBeforeUtc) return Terminal(operation, operation.ExecuteBeforeUtc, "operationExecutionExpired", 409);
        if (deleted) return Terminal(operation, now, "locationDeleted", 410);
        if (values == null) return Terminal(operation, now, "locationUnavailable", 404);
        var uuid = values.Value.GetProperty("uuid").GetProperty("value");
        if ((uuid.ValueKind == JsonValueKind.Null ? null : uuid.GetString()) != operation.Uuid?.ToString("D"))
            return Terminal(operation, now, "locationUnavailable", 404);
        var stale = new List<string>();
        foreach (var group in command.Expected.EnumerateObject())
        {
            var current = values.Value.GetProperty(group.Name == "content" ? "deleteContent" : group.Name);
            if (group.Value.GetProperty("state").GetString() != "known" || current.GetProperty("state").GetString() != "known")
                return Terminal(operation, now, "baseUnavailable", 422);
            try
            {
                if (!LocationMutationCommand.Equal(group.Name, group.Value.GetProperty("value"), current.GetProperty("value"))) stale.Add(group.Name);
            }
            catch (MutationProtocolException error) when (error.Code == "baseUnavailable")
            { return Terminal(operation, now, "baseUnavailable", 422); }
        }
        if (stale.Count > 0) return Terminal(operation, now, "locationBaseStale", 409, stale.ToArray());
        if (command.Kind == "update" && command.Changes.TryGetProperty("activity", out var activity)
            && activity.GetProperty("op").GetString() == "set"
            && await new ActivityCatalog(db).ResolveIdAsync(activity.GetProperty("id").GetInt32(), token) == null)
            return Terminal(operation, now, "activityInvalid", 422);
        return null;
    }

    /// <summary>Ordinary detail and final comparison use one SQL snapshot and the identical original encoding.</summary>
    private async Task<JsonElement?> ValuesAsync(string owner, int id, CancellationToken token)
    {
        var detail = await HistoryDetail.ReadAsync(db, owner, id, DateTimeZone.Utc, token, protection);
        return detail == null ? null : JsonSerializer.SerializeToElement(detail).GetProperty("locationMutation").GetProperty("baseValues");
    }

    /// <summary>Only actual coordinate movement reserves optional provider work; value-equal closure does not contact.</summary>
    private static bool Movement(LocationMutationCommand command, JsonElement? values) => command.Kind == "update"
        && command.Changes.TryGetProperty("position", out var position) && values != null
        && (position.GetProperty("latitude").GetDouble() != values.Value.GetProperty("position").GetProperty("value").GetProperty("latitude").GetDouble()
            || position.GetProperty("longitude").GetDouble() != values.Value.GetProperty("position").GetProperty("value").GetProperty("longitude").GetDouble());

    /// <summary>The account fence precedes operation then provider then Location locks everywhere.</summary>
    private async Task<LocationMutationOperation?> FindAsync(string owner, Guid baseId, Guid actionId, CancellationToken token) =>
        await db.LocationMutationOperations.FromSqlInterpolated($$"""
            SELECT * FROM "LocationMutationOperations" WHERE "UserId" = {{owner}} AND "BaseId" = {{baseId}} AND "ActionId" = {{actionId}} FOR UPDATE
            """).SingleOrDefaultAsync(token);

    /// <summary>Expiry uses the real database clock after lock waits, never transaction-start time.</summary>
    internal static Task<DateTime> NowAsync(ApplicationDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTime>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);

    /// <summary>A failed/ambiguous COMMIT escapes as unknown; it is never replaced by a rejected receipt.</summary>
    private async Task<LocationMutationReply> CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        LocationMutationReply result, CancellationToken token)
    {
        await db.SaveChangesAsync(token);
        if (transaction != null) await transaction.CommitAsync(token);
        return result;
    }

    /// <summary>Locks current provider authority before the final Location lock; independent changes discard optional output.</summary>
    private async Task LockProviderAsync(ReverseGeocodingResult? result, string owner, CancellationToken token)
    {
        if (result?.Authority == null) return;
        await db.Database.SqlQuery<int>($$"""
            SELECT 1 AS "Value" FROM "PersonalLocationProviderSelections" WHERE "UserId" = {{owner}} FOR SHARE
            """).ToListAsync(token);
        await db.Database.SqlQuery<int>($$"""
            SELECT 1 AS "Value" FROM "PersonalLocationProviderProfiles" WHERE "UserId" = {{owner}} AND "ProviderKey" = {{result.Authority.ProviderKey}} FOR SHARE
            """).ToListAsync(token);
    }
}
