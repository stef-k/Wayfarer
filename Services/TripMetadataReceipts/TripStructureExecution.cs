using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Executes one protected typed write with the operation fence outside the domain lock order.</summary>
internal sealed class TripStructureExecution(ApplicationDbContext db, TripMetadataPermit permit,
    Func<ApplicationUser, CancellationToken, Task<bool>> credential)
{
    private readonly TripMetadataReceiptStore _store = new(db);
    private TripMetadataIdentity Identity => permit.Identity;

    /// <summary>Checks incoming authority and current database admission while the owner holds its domain locks.</summary>
    private async Task<bool> ReadmitAsync(CancellationToken token)
    {
        await TripMetadataOperations.RequireActiveAsync(db, Identity.SubjectId, credential, token);
        permit.RequireOpen(await _store.AdmissionAsync(token));
        return true;
    }

    /// <summary>Reads a prior winner or commits the domain result and its evidence in one transaction.</summary>
    internal async Task<(TripMetadataEvidence Evidence, bool NewWrite)> ExecuteAsync(byte[] body,
        Func<JsonElement, Func<CancellationToken, Task<bool>>, Task<TripStructureResult>> apply, CancellationToken token)
    {
        if (!string.Equals(Identity.StructureDigest(body), Identity.RequestDigest, StringComparison.Ordinal))
            throw new TripMetadataMutationRejected(409, "trip-attempt-digest-mismatch", "The submitted bytes do not match the Trip save proof.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await _store.FenceAsync(Identity.SubjectId, Identity.OperationId, true, token);
        var prior = await _store.ReadAsync(Identity, permit.Authority, token);
        if (prior != null)
        {
            await TripMetadataOperations.RequireActiveAsync(db, Identity.SubjectId, credential, token);
            permit.RequireCurrent(await _store.AdmissionAsync(token));
            await transaction.CommitAsync(token);
            return (prior, false);
        }

        var evidence = await ApplyAsync(transaction, body, apply, token);
        var completion = await _store.AdmissionAsync(token);
        permit.RequireCurrent(completion);
        evidence = evidence with
        {
            CompletedAtUtc = completion.Now,
            ReceiptRetainUntilUtc = (completion.Now > Identity.ExecuteBeforeUtc ? completion.Now : Identity.ExecuteBeforeUtc).AddDays(30)
        };
        await _store.RecordAsync(Identity, permit.Authority, evidence, token);
        var stored = await _store.ReadAsync(Identity, permit.Authority, token) ?? throw TripMetadataOperations.Unavailable();
        await transaction.CommitAsync(token);
        return (stored, evidence.State == "applied");
    }

    /// <summary>Preserves the operation fence while removing staged effects from a bounded rejected write.</summary>
    private async Task<TripMetadataEvidence> ApplyAsync(IDbContextTransaction transaction, byte[] body,
        Func<JsonElement, Func<CancellationToken, Task<bool>>, Task<TripStructureResult>> apply, CancellationToken token)
    {
        const string savepoint = "trip_structure_effects";
        await transaction.CreateSavepointAsync(savepoint, token);
        try
        {
            var result = await apply(TripStructureBodies.ReadWrite(body), ReadmitAsync);
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
        catch (Exception error) when (error.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
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
    JsonElement Payload, Func<CancellationToken, Task<bool>> Readmit, CancellationToken Token);
