using System.Text.Json;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationMutations;

/// <summary>Typed transport result; only retained terminal envelopes attribute effects to an action.</summary>
internal sealed record LocationMutationReply(int Status, object Body);

/// <summary>Builds compact immutable receipts and explicitly non-receipt absence observations.</summary>
internal static class LocationMutationReceipts
{
    internal const string Contract = "location-mutation-1";

    /// <summary>Recover a terminal row before any current target/catalog/normalization decision.</summary>
    internal static LocationMutationReply Observe(LocationMutationOperation row, DateTime now)
    {
        if (row.ResultJson != null) return new(row.ResultStatus!.Value, JsonSerializer.Deserialize<JsonElement>(row.ResultJson));
        if (now >= row.ExecuteBeforeUtc)
            return Terminal(row, row.ExecuteBeforeUtc, "operationExecutionExpired", 409, persist: false);
        var body = Common(row);
        body["state"] = "pending"; body["observedAtUtc"] = now; body["executeBeforeUtc"] = row.ExecuteBeforeUtc;
        body["futureEffects"] = true; body["retryAfterSeconds"] = 2;
        return new(202, body);
    }

    /// <summary>Records rejection or applied effects in the caller's transaction; completion is immutable thereafter.</summary>
    internal static LocationMutationReply Terminal(LocationMutationOperation row, DateTime now, string? code = null,
        int status = 200, string[]? stale = null, bool changed = false, string address = "unchanged", string provider = "notRequested", bool persist = true)
    {
        var body = Common(row);
        body["state"] = code == null ? "applied" : "rejected";
        body["completedAtUtc"] = now; body["futureEffects"] = false;
        if (code != null)
        {
            body["code"] = code;
            if (stale != null) body["staleGroups"] = stale;
        }
        else
        {
            body["changed"] = changed; body["detailUrl"] = row.Kind == "delete" ? null : $"/api/location/history/{row.LocationId}";
            if (row.Kind == "update") { body["addressOutcome"] = address; body["providerOutcome"] = provider; }
        }
        if (persist)
        { row.State = code == null ? "applied" : "rejected"; row.ResultJson = JsonSerializer.Serialize(body); row.ResultStatus = status; }
        return new(status, body);
    }

    /// <summary>No-row evidence never invents an action fingerprint, kind, adapter or historical result.</summary>
    internal static LocationMutationReply Unknown(string owner, int id, Guid baseId, Guid actionId, DateTime now, LocationMutationBase? proof)
    {
        var body = new Dictionary<string, object?>
        {
            ["contract"] = Contract, ["subjectId"] = owner, ["locationId"] = id, ["baseId"] = baseId, ["actionId"] = actionId,
            ["observedAtUtc"] = now, ["state"] = "unknown", ["code"] = proof == null ? "operationProofUnavailable"
                : now >= proof.ExpiresAtUtc ? "operationHistoryExpired" : "operationNotObserved",
            ["futureEffects"] = proof == null ? null : now < proof.ExpiresAtUtc
        };
        if (proof != null)
        {
            body["expiresAtUtc"] = proof.ExpiresAtUtc;
            if (now >= proof.ExpiresAtUtc) body["historicalOutcome"] = "unknown";
        }
        return new(200, body);
    }

    /// <summary>Every known-row response exposes exactly the same immutable correlation binding.</summary>
    private static Dictionary<string, object?> Common(LocationMutationOperation row) => new()
    {
        ["contract"] = Contract, ["normalization"] = row.Normalization, ["adapter"] = row.Adapter,
        ["subjectId"] = row.UserId, ["locationId"] = row.LocationId, ["uuid"] = row.Uuid,
        ["baseId"] = row.BaseId, ["actionId"] = row.ActionId, ["kind"] = row.Kind, ["fingerprint"] = row.Fingerprint,
        ["receiptRetainUntilUtc"] = row.ReceiptRetainUntilUtc
    };

    /// <summary>Unscoped failures are problems, never fabricated receipts.</summary>
    internal static LocationMutationReply Problem(string code, int status) => new(status, new { code, status, title = code });
}
