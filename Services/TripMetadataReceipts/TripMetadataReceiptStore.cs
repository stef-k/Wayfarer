using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>PostgreSQL authority, clock, operation fence and finite history shared across adapters and maintenance.</summary>
internal sealed class TripMetadataReceiptStore(ApplicationDbContext db)
{
    /// <summary>Reads the singleton and a wall-clock sample in one statement, after any earlier lock waits.</summary>
    internal async Task<TripMetadataAdmission> AdmissionAsync(CancellationToken token)
    {
        try
        {
            var sample = await db.Database.SqlQuery<TripMetadataAdmission>($"""
                SELECT authority_id AS "Authority", retired_through AS "Cutoff", clock_timestamp() AS "Now"
                FROM public.trip_metadata_proof_authority WHERE singleton
                    AND to_regclass('public.trip_metadata_receipts') IS NOT NULL
                """).SingleOrDefaultAsync(token);
            return sample is { Authority: var authority } && authority != Guid.Empty
                ? sample : throw TripMetadataOperations.Unavailable();
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw TripMetadataOperations.Unavailable();
        }
    }

    /// <summary>Linearizes one account/operation identity until the owning transaction ends.</summary>
    internal async Task<bool> FenceAsync(string subject, Guid operation, bool wait, CancellationToken token)
    {
        var key = "Wayfarer.TripMetadata:" + operation.ToString("D") + ":" + subject;
        if (!wait)
            return await db.Database.SqlQuery<bool>($"""
                SELECT pg_try_advisory_xact_lock(hashtextextended({key}, 0)) AS "Value"
                """).SingleAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
        return true;
    }

    /// <summary>Reads committed terminal evidence after taking the operation fence.</summary>
    internal async Task<TripMetadataEvidence?> ReadAsync(TripMetadataIdentity identity, Guid authority, CancellationToken token)
    {
        var stored = await db.Database.SqlQuery<TripMetadataStoredEvidence>($"""
            SELECT correlation::text AS "Correlation", evidence::text AS "Evidence"
            FROM public.trip_metadata_receipts
            WHERE subject_id = {identity.SubjectId} AND operation_id = {identity.OperationId} AND authority_id = {authority}
            """).SingleOrDefaultAsync(token);
        if (stored == null) return null;
        if (JsonSerializer.Deserialize<TripMetadataIdentity>(stored.Correlation, JsonSerializerOptions.Web) != identity)
            throw new TripMetadataMutationRejected(409, "trip-attempt-mismatch", "The Trip save proof does not match the recorded operation.");
        return JsonSerializer.Deserialize<TripMetadataEvidence>(stored.Evidence, JsonSerializerOptions.Web)
            ?? throw TripMetadataOperations.Unavailable();
    }

    /// <summary>Stages the receipt in the same transaction as the authoritative field write.</summary>
    internal Task RecordAsync(TripMetadataIdentity identity, Guid authority, TripMetadataEvidence evidence, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.trip_metadata_receipts (subject_id, operation_id, authority_id, execute_before, retain_until, correlation, evidence)
            VALUES ({identity.SubjectId}, {identity.OperationId}, {authority}, {identity.ExecuteBeforeUtc},
                {evidence.ReceiptRetainUntilUtc}, {JsonSerializer.Serialize(identity, JsonSerializerOptions.Web)}::jsonb,
                {JsonSerializer.Serialize(evidence, JsonSerializerOptions.Web)}::jsonb)
            """, token);

    /// <summary>Advances the durable cutoff independently of history or outstanding writers.</summary>
    internal Task AdvanceCutoffAsync(CancellationToken token) => db.Database.ExecuteSqlRawAsync("""
        UPDATE public.trip_metadata_proof_authority
        SET retired_through = GREATEST(retired_through, clock_timestamp()) WHERE singleton
        """, token);

    /// <summary>Scans only currently eligible history and takes each operation fence before rechecking deletion.</summary>
    internal async Task CleanupAsync(CancellationToken token)
    {
        var eligible = await db.Database.SqlQuery<TripMetadataCleanupCandidate>($"""
            SELECT subject_id AS "Subject", operation_id AS "Operation"
            FROM public.trip_metadata_receipts, public.trip_metadata_proof_authority
            WHERE singleton AND execute_before <= retired_through AND retain_until <= clock_timestamp()
            ORDER BY retain_until, operation_id, subject_id LIMIT 200
            """).ToListAsync(token);
        foreach (var candidate in eligible)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (!await FenceAsync(candidate.Subject, candidate.Operation, false, token)) continue;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM public.trip_metadata_receipts USING public.trip_metadata_proof_authority
                WHERE singleton AND subject_id = {candidate.Subject} AND operation_id = {candidate.Operation}
                    AND execute_before <= retired_through AND retain_until <= clock_timestamp()
                """, token);
            await transaction.CommitAsync(token);
        }
        await new TripStructureClaims(db).CleanupAsync(this, token);
    }
}

/// <summary>One committed continuous database authority and its sampled admission boundaries.</summary>
internal sealed record TripMetadataAdmission(Guid Authority, DateTime Cutoff, DateTime Now);

/// <summary>Identity of a cleanup candidate; final eligibility is checked under the operation fence.</summary>
internal sealed record TripMetadataCleanupCandidate(string Subject, Guid Operation);

/// <summary>Independent persisted correlation and bounded outcome for one terminal record.</summary>
internal sealed record TripMetadataStoredEvidence(string Correlation, string Evidence);
