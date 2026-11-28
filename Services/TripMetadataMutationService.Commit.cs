using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Services;

internal sealed partial class TripMetadataMutationService
{
    /// <summary>Owns the operation fence, final authority checks and atomic evidence commit.</summary>
    private async Task<TripMetadataExecution> CommitAsync(Guid tripId, string subject, TripMetadataPermit? permit,
        Func<ApplicationUser, CancellationToken, Task<bool>>? credential,
        Func<Trip, Func<Task>, Task<TripMetadataExecution>> apply, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var commitStarted = false;
        try
        {
            var store = new TripMetadataReceiptStore(db);
            if (permit != null && await ReadPriorAsync(store, permit, credential, token) is { } prior)
                return new(null, prior);

            var trip = await TripTagMembership.LockTripAsync(db, tripId, subject, token) ?? throw MissingTrip();
            async Task Readmission()
            {
                await TripMetadataOperations.RequireActiveAsync(db, subject, credential, token);
                if (permit != null) permit.RequireOpen(await store.AdmissionAsync(token));
            }
            await Readmission();
            var execution = permit == null ? await apply(trip, Readmission)
                : await AttemptAsync(transaction, permit, () => apply(trip, Readmission), Readmission, token);
            if (permit != null)
                execution = await RecordAsync(store, permit, execution, token);

            commitStarted = true;
            await transaction.CommitAsync(token);
            return execution;
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackFailure) { throw new AggregateException("Trip edit failed and rollback also failed.", original, rollbackFailure); }
            if (!commitStarted && IsContention(original)) throw Contention();
            throw;
        }
    }

    /// <summary>Rolls back all staged effects before recording a bounded refusal under the same fence.</summary>
    private async Task<TripMetadataExecution> AttemptAsync(IDbContextTransaction transaction, TripMetadataPermit permit,
        Func<Task<TripMetadataExecution>> apply, Func<Task> readmission, CancellationToken token)
    {
        const string effects = "trip_receipt_effects";
        await transaction.CreateSavepointAsync(effects, token);
        try { return await apply(); }
        catch (Exception error) when (TerminalCode(error, permit.Identity.Contract) != null)
        {
            await transaction.RollbackToSavepointAsync(effects, token);
            db.ChangeTracker.Clear();
            await readmission();
            return new(null, new("terminalUnapplied", "unapplied", false)
            {
                Code = TerminalCode(error, permit.Identity.Contract),
                Errors = (error as TripMetadataMutationRejected)?.Errors
            });
        }
    }

    /// <summary>Restricts terminal evidence to the refusal grammar of the retained contract.</summary>
    private static string? TerminalCode(Exception error, string contract)
    {
        if (error is TripMetadataMutationRejected { Code: "mutation-base-stale" }) return "mutation-base-stale";
        if (contract != TripMetadataIdentity.Version2) return null;
        if (IsContention(error)) return "mutation-contention";
        if (error is TripMetadataMutationRejected { Code: "mutation-request-invalid", Errors: not null })
            return "mutation-request-invalid";
        return error is TripMetadataMutationRejected { Code: "publication-policy-required" or "share-progress-requires-public"
            or "tag-not-on-trip" or "mutation-contention" } refusal ? refusal.Code : null;
    }

    /// <summary>Only confirmed transaction conflicts count as contention.</summary>
    private static bool IsContention(Exception error) => error.GetBaseException() is PostgresException
        { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };

    /// <summary>Returns a bounded zero-effect contention refusal.</summary>
    internal static TripMetadataMutationRejected Contention() =>
        new(409, "mutation-contention", "Trip editing contention prevented this attempt. Review current values before saving.");

    /// <summary>Historical evidence is independent of present target ownership.</summary>
    private async Task<TripMetadataEvidence?> ReadPriorAsync(TripMetadataReceiptStore store, TripMetadataPermit permit,
        Func<ApplicationUser, CancellationToken, Task<bool>>? credential, CancellationToken token)
    {
        var identity = permit.Identity;
        await store.FenceAsync(identity.SubjectId, identity.OperationId, true, token);
        var prior = await store.ReadAsync(identity, permit.Authority, token);
        if (prior == null) return null;
        await TripMetadataOperations.RequireActiveAsync(db, identity.SubjectId, credential, token);
        permit.RequireCurrent(await store.AdmissionAsync(token));
        return prior;
    }

    /// <summary>Stamps terminal evidence against the authoritative database clock and retention rule.</summary>
    private static async Task<TripMetadataExecution> RecordAsync(TripMetadataReceiptStore store, TripMetadataPermit permit,
        TripMetadataExecution execution, CancellationToken token)
    {
        var completion = await store.AdmissionAsync(token);
        permit.RequireCurrent(completion);
        var deadline = permit.Identity.ExecuteBeforeUtc;
        var evidence = (execution.Evidence ?? throw new InvalidOperationException("Protected execution must produce evidence.")) with
        {
            CompletedAtUtc = completion.Now,
            ReceiptRetainUntilUtc = (completion.Now > deadline ? completion.Now : deadline).AddDays(30)
        };
        await store.RecordAsync(permit.Identity, permit.Authority, evidence, token);
        return execution with { Evidence = evidence };
    }
}
