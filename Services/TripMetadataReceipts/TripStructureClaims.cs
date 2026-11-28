using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Stores a server-only executor claim under the shared account and operation fence.</summary>
internal sealed class TripStructureClaims(ApplicationDbContext db)
{
    /// <summary>Reads a claim only under the operation fence and verifies its full correlation.</summary>
    internal async Task<byte[]?> ReadAsync(TripMetadataPermit permit, CancellationToken token)
    {
        var identity = permit.Identity;
        var row = await db.Database.SqlQuery<Claim>($"""
            SELECT correlation::text AS "Correlation", nonce_digest AS "NonceDigest"
            FROM public.trip_structure_executor_claims
            WHERE subject_id = {identity.SubjectId} AND operation_id = {identity.OperationId} AND authority_id = {permit.Authority}
            """).SingleOrDefaultAsync(token);
        if (row == null) return null;
        if (JsonSerializer.Deserialize<TripMetadataIdentity>(row.Correlation, JsonSerializerOptions.Web) != identity)
            throw TripMetadataOperations.Unavailable();
        return row.NonceDigest;
    }

    /// <summary>Inserts after preflight and locked incoming authority checks, before any provider work.</summary>
    internal Task RecordAsync(TripMetadataPermit permit, byte[] nonce, CancellationToken token)
    {
        var id = permit.Identity;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.trip_structure_executor_claims
                (subject_id, operation_id, authority_id, execute_before, correlation, nonce_digest)
            VALUES ({id.SubjectId}, {id.OperationId}, {permit.Authority}, {id.ExecuteBeforeUtc},
                {JsonSerializer.Serialize(id, JsonSerializerOptions.Web)}::jsonb, {SHA256.HashData(nonce)})
            """, token);
    }

    /// <summary>Consumes the validated claim atomically with terminal settlement.</summary>
    internal Task ConsumeAsync(TripMetadataPermit permit, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync($"""
        DELETE FROM public.trip_structure_executor_claims
        WHERE subject_id = {permit.Identity.SubjectId} AND operation_id = {permit.Identity.OperationId}
            AND authority_id = {permit.Authority}
        """, token);

    /// <summary>Reclaims only claims excluded by cutoff or a superseding proof authority, under their operation fences.</summary>
    internal async Task CleanupAsync(TripMetadataReceiptStore store, CancellationToken token)
    {
        var candidates = await db.Database.SqlQuery<TripMetadataCleanupCandidate>($"""
            SELECT subject_id AS "Subject", operation_id AS "Operation"
            FROM public.trip_structure_executor_claims AS claim, public.trip_metadata_proof_authority AS authority
            WHERE authority.singleton AND (claim.authority_id <> authority.authority_id OR claim.execute_before <= authority.retired_through)
            ORDER BY claim.execute_before, claim.operation_id, claim.subject_id LIMIT 200
            """).ToListAsync(token);
        foreach (var candidate in candidates)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (!await store.FenceAsync(candidate.Subject, candidate.Operation, false, token)) continue;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM public.trip_structure_executor_claims AS claim USING public.trip_metadata_proof_authority AS authority
                WHERE authority.singleton AND claim.subject_id = {candidate.Subject} AND claim.operation_id = {candidate.Operation}
                    AND (claim.authority_id <> authority.authority_id OR claim.execute_before <= authority.retired_through)
                """, token);
            await transaction.CommitAsync(token);
        }
    }

    private sealed record Claim(string Correlation, byte[] NonceDigest);
}
