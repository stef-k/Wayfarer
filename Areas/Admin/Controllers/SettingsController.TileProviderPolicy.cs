using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Admin.Controllers;

/// <summary>Owns the provider-policy validation and historical-budget workflow.</summary>
public partial class SettingsController
{
    /// <summary>Applies authoritative custom-provider cross-field validation.</summary>
    private void ValidateTileProviderPolicy(ApplicationSettings settings)
    {
        if (settings.TileTrafficMode != TileTrafficMode.Custom)
        {
            if (string.Equals(settings.TileProviderKey, TileProviderCatalog.CustomProviderKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                settings.TileProviderAdvancedLimitsEnabled = false;
            }
            return;
        }

        if (!string.Equals(settings.TileProviderKey, TileProviderCatalog.CustomProviderKey,
                StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(ApplicationSettings.TileTrafficMode),
                "Provider Agreement mode requires a compatible Custom provider.");
            return;
        }

        settings.TileProviderAdvancedLimitsEnabled = true;

        try
        {
            TileProviderPolicyResolver.ValidateCustom(settings);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(nameof(ApplicationSettings.TileProviderAdvancedLimitsEnabled),
                exception.Message);
        }
    }

    /// <summary>Explicitly applies the current recommended outbound per-client value only.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UseRecommendedTileOutboundBudget()
    {
        var settings = await _dbContext.ApplicationSettings.FindAsync(1);
        if (settings != null && settings.TileOutboundBudgetPerIpPerMinute == 30)
        {
            settings.TileOutboundBudgetPerIpPerMinute =
                ApplicationSettings.DefaultTileOutboundBudgetPerIpPerMinute;
            settings.TileOutboundBudgetHistorical30Acknowledged = false;
            await _dbContext.SaveChangesAsync();
            LogAudit(
                "TileOutboundBudgetRecommendedApplied",
                "TileOutboundBudgetPerIpPerMinute",
                "30 -> 80");
            _settingsService.RefreshSettings();
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Explicitly retains 30 and persists acknowledgement of the historical notice.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcknowledgeHistoricalTileOutboundBudget()
    {
        var settings = await _dbContext.ApplicationSettings.FindAsync(1);
        if (settings != null &&
            settings.TileOutboundBudgetPerIpPerMinute == 30 &&
            !settings.TileOutboundBudgetHistorical30Acknowledged)
        {
            settings.TileOutboundBudgetHistorical30Acknowledged = true;
            await _dbContext.SaveChangesAsync();
            LogAudit(
                "TileOutboundBudgetHistorical30Acknowledged",
                "TileOutboundBudgetHistorical30Acknowledged",
                "False -> True");
            _settingsService.RefreshSettings();
        }
        return RedirectToAction(nameof(Index));
    }
}
