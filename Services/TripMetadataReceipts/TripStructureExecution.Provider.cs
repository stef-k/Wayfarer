using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.TripMetadataReceipts;

internal sealed partial class TripStructureExecution
{
    private readonly TripStructureClaims _claims = new(db);

    /// <summary>Confirms one durable claim before doing provider work without an open transaction.</summary>
    private async Task<ProviderClaim> ClaimAsync(JsonElement payload, PrepareTripPlaceProvider prepare, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await _store.FenceAsync(Identity.SubjectId, Identity.OperationId, true, token);
        var prior = await _store.ReadAsync(Identity, permit.Authority, token);
        if (prior != null)
        {
            await RequireAccountAsync(token);
            permit.RequireCurrent(await _store.AdmissionAsync(token));
            await transaction.CommitAsync(token);
            return new(prior, null, null);
        }

        var claimed = await _claims.ReadAsync(permit, token);
        if (claimed != null)
        {
            await RequireAccountAsync(token);
            var sample = await _store.AdmissionAsync(token);
            permit.RequireCurrent(sample);
            var evidence = TripMetadataOperations.Observation(true, sample, Identity.ExecuteBeforeUtc);
            await transaction.CommitAsync(token);
            return new(evidence, null, null);
        }

        Func<Task<TripPlaceProviderResult>> resolve;
        try { resolve = await prepare(payload, RequireAccountAsync); }
        catch (TripMetadataMutationRejected error) when (TerminalStatus(error.Code) != null)
        {
            await ReadmitAsync(token);
            var evidence = await RecordAsync(new("terminalUnapplied", "unapplied", false) { Code = error.Code }, token);
            await transaction.CommitAsync(token);
            return new(evidence, null, null);
        }
        await ReadmitAsync(token);
        var nonce = RandomNumberGenerator.GetBytes(32);
        await _claims.RecordAsync(permit, nonce, token);
        await transaction.CommitAsync(token);
        return new(null, nonce, resolve);
    }

    /// <summary>Requires the original in-memory nonce after reacquiring the operation fence.</summary>
    private async Task RequireClaimAsync(byte[]? nonce, CancellationToken token)
    {
        if (nonce == null) return;
        byte[]? digest;
        if (independent == null) digest = await _claims.ReadAsync(permit, token);
        else
        {
            await using var current = await independent.CreateDbContextAsync(token);
            digest = await new TripStructureClaims(current).ReadAsync(permit, token);
        }
        if (digest == null || !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(nonce)))
        {
            // Account deletion can cascade a claim before this invocation gets the operation fence.
            await RequireAccountAsync(token);
            throw TripMetadataOperations.Unavailable();
        }
    }

    /// <summary>Revalidates incoming account authority under the transaction chosen by the caller.</summary>
    private Task RequireAccountAsync(CancellationToken token) =>
        TripMetadataOperations.RequireActiveAsync(db, Identity.SubjectId, credential, token);

    /// <summary>Only an explicit true for the applicable provider-bearing routes starts a claim.</summary>
    private JsonElement? ProviderPayload(byte[] body)
    {
        try
        {
            var payload = TripStructureBodies.ReadWrite(body);
            var key = Identity.Kind == "place-create" ? "value" : "changes";
            return payload.TryGetProperty(key, out var input) && input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("reverseGeocode", out var selection) && selection.ValueKind == JsonValueKind.True ? payload : null;
        }
        catch (TripMetadataMutationRejected) { return null; }
    }

    private sealed record ProviderClaim(TripMetadataEvidence? Evidence, byte[]? Nonce, Func<Task<TripPlaceProviderResult>>? Resolve);
}

/// <summary>Freezes one resolver result, including the allocated identity for a Place append.</summary>
internal sealed record TripPlaceProviderResult(Guid PlaceId, PlaceStructuralAddress Address);

/// <summary>Preflights owned originals and returns provider work to start only after confirmed claim commit.</summary>
internal delegate Task<Func<Task<TripPlaceProviderResult>>> PrepareTripPlaceProvider(JsonElement payload,
    Func<CancellationToken, Task> checkMeteredAuthority);
