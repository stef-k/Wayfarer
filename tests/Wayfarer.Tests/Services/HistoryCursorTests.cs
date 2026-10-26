using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Cryptographic binding, including retired interpretation identity, without a database or live rules mutation.</summary>
public sealed class HistoryCursorTests
{
    /// <summary>Version/rules/owner/profile/order changes fail generically, and retained value anchors round-trip exactly.</summary>
    [Fact]
    public void UnknownAndObsoletePayloadsCannotRestartOrCrossScopes()
    {
        var provider = new EphemeralDataProtectionProvider();
        var cursors = new HistoryCursor(provider);
        var query = HistoryQueryTests.Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-01-01T00:00:00Z&endUtc=2026-01-01T00:00:00Z");
        var key = HistoryCursor.ScopeKey("owner", query.Scope);
        var instant = new DateTime(2025, 10, 10, 3, 0, 0, DateTimeKind.Utc).AddTicks(1234560);
        var valid = new CursorPayload(1, "owner", key, "matches", "owner-summary-1", HistoryCursor.Order, 100, 5, instant);
        var protector = provider.CreateProtector("Wayfarer.LocationHistory.Continuation.v1");
        foreach (var payload in new[] { valid with { Version = 2 }, valid with { ScopeKey = "previous-rules-generation" },
                     valid with { Owner = "other" }, valid with { Profile = "owner-unresolved-1" }, valid with { Order = "id-desc" } })
        {
            query = WithCursor(query, protector.Protect(JsonSerializer.Serialize(payload)));
            Assert.Equal("continuationInvalid", Assert.Throws<HistoryRequestException>(() => cursors.Read("owner", query, key)).Code);
        }
        query = WithCursor(query, cursors.Protect("owner", query, key, 5, instant));
        Assert.Equal(instant, cursors.Read("owner", query, key)!.EventUtc);
    }

    /// <summary>Either real semantic transition retires old continuations, even when that generation is unused by a filter.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IndependentSemanticChangesRejectPreviousContinuations(bool timeChanged)
    {
        var cursors = new HistoryCursor(new EphemeralDataProtectionProvider());
        var query = HistoryQueryTests.Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-01-01T00:00:00Z&endUtc=2026-01-01T00:00:00Z");
        var previous = HistoryCursor.Hash(JsonSerializer.Serialize(new
        {
            owner = "owner", scope = query.Scope, rules = HistoryCursor.Rules,
            time = timeChanged ? "history-time-1;GeoTimeZone-6.1.0;TZDB: 2026c (mapping: 48.2)" : LocationHistoryDerivation.TimeVersion,
            text = timeChanged ? LocationHistoryDerivation.TextVersion : "history-text-1;RichNotes-2;AngleSharp-1.7.1.0;runtime-10.0.13;unicode-31129-0000799900000000000000000000007f"
        }));
        var cursor = cursors.Protect("owner", query, previous, 5, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var continued = WithCursor(query, cursor);
        Assert.Equal("continuationInvalid", Assert.Throws<HistoryRequestException>(() =>
            cursors.Read("owner", continued, HistoryCursor.ScopeKey("owner", continued.Scope))).Code);
    }

    private static HistoryQuery WithCursor(HistoryQuery query, string cursor) => new()
    {
        Scope = query.Scope, Zone = query.Zone, Start = query.Start, End = query.End, Continuation = cursor
    };
}
