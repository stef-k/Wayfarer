using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Admin.Controllers;

/// <summary>Owns the provider-policy validation and historical-budget workflow.</summary>
public partial class SettingsController
{
    /// <summary>Copies every persisted provider recovery field without normalization.</summary>
    private static void PreserveTileProviderRecoveryState(
        ApplicationSettings currentSettings,
        ApplicationSettings updatedSettings)
    {
        updatedSettings.TileProviderKey = currentSettings.TileProviderKey;
        updatedSettings.TileProviderUrlTemplate = currentSettings.TileProviderUrlTemplate;
        updatedSettings.TileProviderAttribution = currentSettings.TileProviderAttribution;
        updatedSettings.TileProviderApiKey = currentSettings.TileProviderApiKey;
        updatedSettings.TileTrafficMode = currentSettings.TileTrafficMode;
        updatedSettings.TileOutboundBudgetPerIpPerMinute = currentSettings.TileOutboundBudgetPerIpPerMinute;
        updatedSettings.TileProviderAdvancedLimitsEnabled = currentSettings.TileProviderAdvancedLimitsEnabled;
        updatedSettings.TileProviderSustainedRequestsPerSecond = currentSettings.TileProviderSustainedRequestsPerSecond;
        updatedSettings.TileProviderBurstCapacity = currentSettings.TileProviderBurstCapacity;
        updatedSettings.TileProviderMaxConcurrency = currentSettings.TileProviderMaxConcurrency;
        updatedSettings.TileProviderMaxAttempts = currentSettings.TileProviderMaxAttempts;
        updatedSettings.TileProviderFallbackBaseDelayMs = currentSettings.TileProviderFallbackBaseDelayMs;
        updatedSettings.TileProviderFallbackDelayCapSeconds = currentSettings.TileProviderFallbackDelayCapSeconds;
        updatedSettings.TileProviderMaxIndividualWaitSeconds = currentSettings.TileProviderMaxIndividualWaitSeconds;
        updatedSettings.TileProviderTotalRetryCeilingSeconds = currentSettings.TileProviderTotalRetryCeilingSeconds;
    }

    /// <summary>Formats the complete bounded non-secret policy state for one audit transition.</summary>
    private static string DescribeTilePolicyForAudit(TileProviderPolicy policy) =>
        $"Mode={policy.TrafficMode}; " +
        $"Compatibility={policy.Compatibility.Status}; " +
        $"CompatibilitySource={policy.Compatibility.AuditSource}; " +
        $"TileEffectiveRate={policy.SustainedRequestsPerSecond}; TileEffectiveRateActive={policy.IsRateActive}; " +
        $"TileEffectiveBurst={policy.BurstCapacity}; TileEffectiveBurstActive={policy.IsBurstActive}; " +
        $"TileEffectiveConcurrency={policy.MaxConcurrency}; TileEffectiveConcurrencyActive={policy.IsConcurrencyActive}; " +
        $"TileEffectiveClientAllowance={policy.ClientSeriesPerMinute}; TileEffectiveClientAllowanceActive={policy.IsClientSeriesAllowanceActive}; " +
        $"TileRetryControlsActive={policy.CanContactProvider}; " +
        $"TileRetryMaxAttempts={policy.MaxAttempts}; " +
        $"TileRetryFallbackBaseDelayMs={policy.FallbackBaseDelay.TotalMilliseconds:0}; " +
        $"TileRetryFallbackDelayCapSeconds={policy.FallbackDelayCap.TotalSeconds:0}; " +
        $"TileRetryMaxIndividualWaitSeconds={policy.MaxIndividualWait.TotalSeconds:0}; " +
        $"TileRetryTotalCeilingSeconds={policy.TotalRetryCeiling.TotalSeconds:0}";

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
