using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Issues and recovers permits under the current adapter and continuous database authority.</summary>
internal sealed class TripMetadataOperations(ApplicationDbContext db, TripMetadataProofs proofs, string subject, bool web,
    Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential)
{
    private readonly TripMetadataReceiptStore _store = new(db);
    private string Adapter => web ? "web" : "bearer";

    /// <summary>Authenticates a new edit without creating a durable execution record or applying effects.</summary>
    internal async Task<Dictionary<string, object?>> IssueAsync(Guid tripId, Stream body, CancellationToken token)
    {
        var digest = await ReadIssuanceAsync(body, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var owned = await db.Trips.FromSqlInterpolated($"""
            SELECT * FROM public."Trips" WHERE "Id" = {tripId} AND "UserId" = {subject} FOR SHARE
            """).AnyAsync(token);
        if (!owned) throw TripMetadataMutationService.MissingTrip();
        await RequireActiveAsync(db, subject, revalidateCredential, token);
        var sample = await _store.AdmissionAsync(token);
        if (sample.Now < sample.Cutoff) throw Unavailable();
        var identity = new TripMetadataIdentity(TripMetadataIdentity.Version, subject, Adapter, TripMetadataIdentity.OperationKind,
            tripId, digest, Guid.NewGuid(), sample.Now.AddMinutes(15));
        var envelope = identity.Envelope();
        envelope.Add("proof", proofs.Protect(identity, sample.Authority));
        await transaction.CommitAsync(token);
        return envelope;
    }

    /// <summary>Checks exact issuance member names, cardinality and digest spelling.</summary>
    private static async Task<string> ReadIssuanceAsync(Stream body, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 2
                && root.TryGetProperty("contract", out var version) && version.ValueKind == JsonValueKind.String
                && version.GetString() == TripMetadataIdentity.Version
                && root.TryGetProperty("requestDigest", out var digest) && digest.ValueKind == JsonValueKind.String
                && digest.GetString() is { } value && TripMetadataIdentity.IsDigest(value))
                return value;
        }
        catch (JsonException) { }
        throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "A supported Trip save contract and request digest are required.");
    }

    /// <summary>Reads the active authority before accepting any proof and binds it to this authenticated route.</summary>
    internal async Task<TripMetadataPermit> VerifyAsync(Guid tripId, string proof, Guid? operationId, CancellationToken token)
    {
        var sample = await _store.AdmissionAsync(token);
        var identity = proofs.Verify(proof, sample.Authority);
        if (identity.SubjectId != subject || identity.Adapter != Adapter || identity.TripId != tripId
            || identity.Kind != TripMetadataIdentity.OperationKind || operationId.HasValue && identity.OperationId != operationId)
            throw new TripMetadataMutationRejected(409, "trip-attempt-mismatch", "The Trip save proof does not match this request.");
        return new(identity, sample.Authority);
    }

    /// <summary>Checks account authority after the operation fence and before returning historical evidence.</summary>
    internal async Task<Dictionary<string, object?>> LookupAsync(Guid tripId, Guid operationId, string proof, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await ObserveAsync(tripId, operationId, proof, budget.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw Unavailable();
        }
    }

    /// <summary>Acquires the fence within budget, then reads committed evidence and the current retirement boundary.</summary>
    private async Task<Dictionary<string, object?>> ObserveAsync(Guid tripId, Guid operationId, string proof, CancellationToken token)
    {
        var permit = await VerifyAsync(tripId, proof, operationId, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var fenced = await _store.FenceAsync(subject, operationId, false, token);
        await RequireActiveAsync(db, subject, revalidateCredential, token);
        var sample = await _store.AdmissionAsync(token);
        permit.RequireCurrent(sample);
        var evidence = fenced ? await _store.ReadAsync(permit.Identity, permit.Authority, token) : null;
        evidence ??= Observation(fenced, sample, permit.Identity.ExecuteBeforeUtc);
        await transaction.CommitAsync(token);
        return evidence.Envelope(permit.Identity);
    }

    /// <summary>Only committed cutoff coverage excludes future effects without retained history.</summary>
    private static TripMetadataEvidence Observation(bool fenced, TripMetadataAdmission sample, DateTime deadline)
    {
        if (!fenced) return new("unresolved", "unknown", null) { ObservedAtUtc = sample.Now, Code = "trip-attempt-in-flight" };
        if (sample.Cutoff >= deadline) return new("retired", "unknown", false) { ObservedAtUtc = sample.Now };
        if (sample.Now >= deadline) return new("unresolved", "unknown", null) { ObservedAtUtc = sample.Now, Code = "trip-attempt-awaiting-retirement" };
        return new("unobserved", "unknown", true) { ObservedAtUtc = sample.Now };
    }

    /// <summary>Retains the writer's account and incoming credential checks through transaction completion.</summary>
    internal static async Task RequireActiveAsync(ApplicationDbContext db, string subject,
        Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential, CancellationToken token)
    {
        var active = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {subject} AND \"IsActive\" FOR SHARE")
            .AsNoTracking().ToListAsync(token);
        if (active.Count == 0 || revalidateCredential != null && !await revalidateCredential(active[0], token))
            throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");
    }

    /// <summary>Signals that this request supplies no operation settlement.</summary>
    internal static TripMetadataMutationRejected Unavailable() =>
        new(503, "trip-attempt-unavailable", "Trip save recovery is temporarily unavailable. Keep your draft and try recovery again.");
}

/// <summary>Verified proof, independently subject to current database authority and execution-time rules.</summary>
internal sealed record TripMetadataPermit(TripMetadataIdentity Identity, Guid Authority)
{
    /// <summary>Refuses evidence from a different database continuity even after a wait.</summary>
    internal void RequireCurrent(TripMetadataAdmission sample)
    {
        if (sample.Authority != Authority) throw TripMetadataOperations.Unavailable();
    }

    /// <summary>Admission closes at either the wall-clock deadline or the committed cutoff.</summary>
    internal void RequireOpen(TripMetadataAdmission sample)
    {
        RequireCurrent(sample);
        if (sample.Now >= Identity.ExecuteBeforeUtc || sample.Cutoff >= Identity.ExecuteBeforeUtc)
            throw new TripMetadataMutationRejected(409, "trip-attempt-window-closed", "The Trip save execution window is closed.");
    }
}
