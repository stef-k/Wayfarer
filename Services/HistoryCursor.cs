using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Opaque continuations grant no authority and need no surviving anchor row.</summary>
internal sealed class HistoryCursor(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Wayfarer.LocationHistory.Continuation.v1");
    internal const string Order = "eventUtc-desc,id-desc";
    internal const string Rules = "history-query-1";

    /// <summary>Response controls are separate from the common map/list matching identity.</summary>
    internal static string ScopeKey(string owner, HistoryScope scope) => Hash(JsonSerializer.Serialize(new
    {
        owner, scope, rules = Rules, time = LocationHistoryDerivation.TimeVersion, text = LocationHistoryDerivation.TextVersion
    }));

    /// <summary>Pin every interpretation/control in the protected payload, including the candidate profile/order.</summary>
    internal string Protect(string owner, HistoryQuery query, string scopeKey, int id, DateTime? eventUtc) =>
        _protector.Protect(JsonSerializer.Serialize(new CursorPayload(1, owner, scopeKey, query.ResultSet,
            Profile(query), query.ResultSet == "matches" ? Order : "id-desc", query.PageSize, id, eventUtc)));

    /// <summary>Any invalid/foreign/obsolete continuation has the same controlled error; never silently restart.</summary>
    internal CursorPayload? Read(string owner, HistoryQuery query, string scopeKey)
    {
        if (query.Continuation == null) return null;
        try
        {
            var value = JsonSerializer.Deserialize<CursorPayload>(_protector.Unprotect(query.Continuation));
            if (value == null || value.Version != 1 || value.Owner != owner || value.ScopeKey != scopeKey
                || value.ResultSet != query.ResultSet || value.Profile != Profile(query) || value.PageSize != query.PageSize
                || value.Order != (query.ResultSet == "matches" ? Order : "id-desc") || value.Id <= 0
                || query.ResultSet == "matches" && (value.EventUtc == null || value.EventUtc.Value.Kind != DateTimeKind.Utc)) throw new FormatException();
            return value;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException or ArgumentException)
        { throw new HistoryRequestException("continuationInvalid"); }
    }

    /// <summary>Matching and inspection profiles cannot share a continuation.</summary>
    internal static string Profile(HistoryQuery query) => query.ResultSet == "matches" ? "owner-summary-1" : "owner-unresolved-1";
    /// <summary>Stable scope/cell identity is a content digest, never an authorization credential.</summary>
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Versioned anchor values, authenticated together with the effective scope and response controls.</summary>
internal sealed record CursorPayload(int Version, string Owner, string ScopeKey, string ResultSet, string Profile,
    string Order, int PageSize, int Id, DateTime? EventUtc);
