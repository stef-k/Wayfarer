using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Services;

public partial class TileCacheService
{
    /// <summary>
    /// Resolves and peeks the captured policy's opaque client-series allowance without charging it.
    /// The caller records the charge only immediately before the first admitted transport contact.
    /// </summary>
    private bool TryPrepareClientSeriesAllowance(
        Wayfarer.Services.TileProviderPolicy policy,
        bool shouldCharge,
        string? capturedOpaqueClientKey,
        bool allowHttpContext,
        out string? allowanceKey)
    {
        allowanceKey = null;
        if (!shouldCharge || policy.ClientSeriesPerMinute <= 0)
            return true;

        allowanceKey = capturedOpaqueClientKey;
        var context = allowHttpContext ? _httpContextAccessor.HttpContext : null;
        if (allowanceKey == null && context != null)
            allowanceKey = ResolveSchedulerClientKey(context, RateLimitHelper.GetClientIpAddress(context));

        if (allowanceKey == null || !RateLimitHelper.WouldExceedRateLimit(
                TilesController.OutboundBudgetCache, allowanceKey, policy.ClientSeriesPerMinute))
            return true;

        TileCacheDiagnostics.ClientBudgetRejected(_logger, "outbound-client");
        _logger.LogWarning("Per-client outbound tile allowance exceeded; upstream request rejected.");
        return false;
    }
}
