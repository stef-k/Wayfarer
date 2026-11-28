using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Executes one protected typed write with the operation fence outside the domain lock order.</summary>
internal sealed partial class TripStructureExecution(ApplicationDbContext db, TripMetadataPermit permit,
    Func<ApplicationUser, CancellationToken, Task<bool>> credential, IDbContextFactory<ApplicationDbContext>? independent = null)
{
    private readonly TripMetadataReceiptStore _store = new(db);
    private TripMetadataIdentity Identity => permit.Identity;

    /// <summary>Checks incoming authority and current database admission while the owner holds its domain locks.</summary>
    private async Task<bool> ReadmitAsync(CancellationToken token)
    {
        await TripMetadataOperations.RequireActiveAsync(db, Identity.SubjectId, credential, token);
        permit.RequireOpen(await ReadCurrentAdmissionAsync(token));
        return true;
    }

    /// <summary>Reads a prior winner or commits the domain result and its evidence in one transaction.</summary>
    internal async Task<(TripMetadataEvidence Evidence, bool NewWrite)> ExecuteAsync(byte[] body,
        ApplyTripStructure apply, CancellationToken token, PrepareTripPlaceProvider? prepareProvider = null)
    {
        if (!string.Equals(Identity.StructureDigest(body), Identity.RequestDigest, StringComparison.Ordinal))
            throw new TripMetadataMutationRejected(409, "trip-attempt-digest-mismatch", "The submitted bytes do not match the Trip save proof.");

        byte[]? nonce = null;
        TripPlaceProviderResult? providerResult = null;
        if (prepareProvider != null && ProviderPayload(body) is { } payload)
        {
            var claim = await ClaimAsync(payload, prepareProvider, token);
            if (claim.Evidence != null) return (claim.Evidence, false);
            nonce = claim.Nonce ?? throw TripMetadataOperations.Unavailable();
            providerResult = await (claim.Resolve ?? throw TripMetadataOperations.Unavailable())();
        }
        try { return await ExecuteFencedAsync(body, apply, nonce, providerResult, token); }
        catch (Exception error) when (independent != null && CheckedCreationService.IsUnappliedContention(error))
        {
            // PostgreSQL reported a definite abort. Observe any committed winner under a new fence.
            return (await SettleCreationContentionAsync(nonce, token), false);
        }
    }

    /// <summary>Uses nonwaiting ownership and independent committed evidence for Serializable creation.</summary>
    private async Task<(TripMetadataEvidence Evidence, bool NewWrite)> ExecuteFencedAsync(byte[] body,
        ApplyTripStructure apply, byte[]? nonce, TripPlaceProviderResult? providerResult, CancellationToken token)
    {
        var serializable = independent != null;
        await using var transaction = await db.Database.BeginTransactionAsync(serializable ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted, token);
        if (!await _store.FenceAsync(Identity.SubjectId, Identity.OperationId, !serializable, token))
            throw TripMetadataOperations.Unavailable();
        var prior = await ReadPriorAsync(token);

        if (prior != null)
        {
            await TripMetadataOperations.RequireActiveAsync(db, Identity.SubjectId, credential, token);
            permit.RequireCurrent(await ReadCurrentAdmissionAsync(token));
            await transaction.CommitAsync(token);
            return (prior, false);
        }

        await RequireClaimAsync(nonce, token);
        var evidence = await ApplyAsync(transaction, body, apply, providerResult, token);
        var stored = await RecordAsync(evidence, token);
        if (nonce != null) await _claims.ConsumeAsync(permit, token);
        await transaction.CommitAsync(token);
        return (stored, evidence.State == "applied");
    }

    /// <summary>Obtains committed evidence even if Serializable took a snapshot during the fence statement.</summary>
    private async Task<TripMetadataEvidence?> ReadPriorAsync(CancellationToken token)
    {
        if (independent == null) return await _store.ReadAsync(Identity, permit.Authority, token);
        await using var current = await independent.CreateDbContextAsync(token);
        var store = new TripMetadataReceiptStore(current);
        permit.RequireCurrent(await store.AdmissionAsync(token));
        return await store.ReadAsync(Identity, permit.Authority, token);
    }

    /// <summary>Samples the clock and durable cutoff outside the domain snapshot for checked creation.</summary>
    private async Task<TripMetadataAdmission> ReadCurrentAdmissionAsync(CancellationToken token)
    {
        if (independent == null) return await _store.AdmissionAsync(token);
        await using var current = await independent.CreateDbContextAsync(token);
        return await new TripMetadataReceiptStore(current).AdmissionAsync(token);
    }

    /// <summary>Persists the bounded receipt and projects its stable stored representation.</summary>
    private async Task<TripMetadataEvidence> RecordAsync(TripMetadataEvidence evidence, CancellationToken token)
    {
        var completion = await ReadCurrentAdmissionAsync(token);
        permit.RequireCurrent(completion);
        evidence = evidence with
        {
            CompletedAtUtc = completion.Now,
            ReceiptRetainUntilUtc = (completion.Now > Identity.ExecuteBeforeUtc ? completion.Now : Identity.ExecuteBeforeUtc).AddDays(30)
        };
        await _store.RecordAsync(Identity, permit.Authority, evidence, token);
        return await _store.ReadAsync(Identity, permit.Authority, token) ?? throw TripMetadataOperations.Unavailable();
    }

    /// <summary>Settles a definitive creation abort after reading the fresh winner and checking current authority.</summary>
    private async Task<TripMetadataEvidence> SettleCreationContentionAsync(byte[]? nonce, CancellationToken token)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await _store.FenceAsync(Identity.SubjectId, Identity.OperationId, true, token);
        var prior = await _store.ReadAsync(Identity, permit.Authority, token);
        await TripMetadataOperations.RequireActiveAsync(db, Identity.SubjectId, credential, token);
        var sample = await _store.AdmissionAsync(token);
        permit.RequireCurrent(sample);
        if (prior != null)
        {
            await transaction.CommitAsync(token);
            return prior;
        }

        permit.RequireOpen(sample);
        await RequireClaimAsync(nonce, token);
        var evidence = await RecordAsync(new("terminalUnapplied", "unapplied", false) { Code = "mutation-contention" }, token);
        if (nonce != null) await _claims.ConsumeAsync(permit, token);
        await transaction.CommitAsync(token);
        return evidence;
    }

    /// <summary>Preserves the operation fence while removing staged effects from a bounded rejected write.</summary>
    private async Task<TripMetadataEvidence> ApplyAsync(IDbContextTransaction transaction, byte[] body,
        ApplyTripStructure apply, TripPlaceProviderResult? providerResult, CancellationToken token)
    {
        const string savepoint = "trip_structure_effects";
        await transaction.CreateSavepointAsync(savepoint, token);
        try
        {
            var result = await apply(TripStructureBodies.ReadWrite(body), ReadmitAsync, providerResult);
            var freshness = await db.Trips.Where(trip => trip.Id == Identity.TripId)
                .Select(trip => trip.UpdatedAt).SingleAsync(token);
            var state = result.Applied ? "applied" : "checkedNoOp";
            return new(state, state, false)
            {
                UpdatedAtUtc = freshness,
                Result = JsonSerializer.SerializeToElement(result.Result, JsonSerializerOptions.Web)
            };
        }
        catch (TripMetadataMutationRejected error) when (TerminalStatus(error.Code) != null)
        {
            await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            db.ChangeTracker.Clear();
            await ReadmitAsync(token);
            return new("terminalUnapplied", "unapplied", false) { Code = error.Code };
        }
        catch (Exception error) when (independent == null && error.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
        {
            await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            db.ChangeTracker.Clear();
            await ReadmitAsync(token);
            return new("terminalUnapplied", "unapplied", false) { Code = "mutation-contention" };
        }
    }

    /// <summary>Limits terminal results to the typed receipt contract's public code and status pairs.</summary>
    internal static int? TerminalStatus(string? code) => code switch
    {
        "mutation-request-invalid" or "region-deletion-request-invalid" or "place-deletion-request-invalid" => 400,
        "mutation-region-protected" or "mutation-operation-forbidden" => 403,
        "mutation-scope-unavailable" => 404,
        "mutation-base-stale" or "mutation-scope-stale" or "mutation-dependency-stale" or "mutation-contention"
            or "mutation-review-required" or "region-deletion-preview-stale" or "place-deletion-preview-stale"
            or "area-deletion-preview-stale" or "unassigned_region_conflict" => 409,
        _ => null
    };
}

/// <summary>Result projected from saved state while the mutation owner still holds its locks.</summary>
internal sealed record TripStructureResult(bool Applied, object Result);

/// <summary>Bound route and parsed frozen body supplied to the current domain mutation owner.</summary>
internal sealed record TripStructureWrite(string Subject, bool Web, Guid TripId, Guid TargetId,
    JsonElement Payload, Func<CancellationToken, Task<bool>> Readmit, CancellationToken Token, TripPlaceProviderResult? Provider = null);

/// <summary>Applies a parsed typed body and any frozen provider outcome within the fenced domain transaction.</summary>
internal delegate Task<TripStructureResult> ApplyTripStructure(JsonElement payload,
    Func<CancellationToken, Task<bool>> readmit, TripPlaceProviderResult? provider);
