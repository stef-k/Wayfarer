using Microsoft.AspNetCore.Mvc;
using Wayfarer.Localization;
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
                _text["AdminSettings.Validation.ModeRequiresCustom"]);
            return;
        }

        settings.TileProviderAdvancedLimitsEnabled = true;

        var errors = TileProviderPolicyResolver.GetCustomValidationErrors(settings);
        if (errors.Count > 0)
        {
            // The existing field owns one combined policy error.
            var explanation = string.Join(" ", errors.Values.Select(error => AdminSettingsWebText.PolicyError(_text, error)).Distinct());
            ModelState.AddModelError(nameof(ApplicationSettings.TileProviderAdvancedLimitsEnabled), explanation);
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

    /// <summary>
    /// Normalizes and validates tile provider settings, applying presets when selected.
    /// </summary>
    private bool NormalizeTileProviderSettings(ApplicationSettings currentSettings, ApplicationSettings updatedSettings)
    {
        var providerKey = updatedSettings.TileProviderKey?.Trim();
        var currentCompatibility = TileProviderCatalog.ResolveCompatibility(
            currentSettings.TileProviderKey,
            currentSettings.TileProviderUrlTemplate);
        var preservesUnsupportedState =
            currentCompatibility.Status != TileProviderCompatibility.Supported &&
            (string.IsNullOrWhiteSpace(providerKey) ||
             string.Equals(providerKey, currentSettings.TileProviderKey?.Trim(),
                 StringComparison.OrdinalIgnoreCase));
        if (preservesUnsupportedState)
        {
            PreserveTileProviderRecoveryState(currentSettings, updatedSettings);
            return true;
        }
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            // Preserve supported provider identity when an unrelated form omits this section.
            PreserveTileProviderRecoveryState(currentSettings, updatedSettings);
            return true;
        }
        var preset = TileProviderCatalog.FindPreset(providerKey);
        var isCustom = string.Equals(providerKey, TileProviderCatalog.CustomProviderKey, StringComparison.OrdinalIgnoreCase);

        if (preset == null && !isCustom)
        {
            ModelState.AddModelError(nameof(ApplicationSettings.TileProviderKey), _text["AdminSettings.Validation.UnknownProvider"]);
            return false;
        }

        if (preset != null)
        {
            updatedSettings.TileProviderKey = preset.Key;
            updatedSettings.TileProviderUrlTemplate = preset.UrlTemplate;
            updatedSettings.TileProviderAttribution = preset.Attribution;
        }
        else
        {
            updatedSettings.TileProviderKey = TileProviderCatalog.CustomProviderKey;
        }

        // Sanitize attribution HTML to prevent XSS attacks.
        updatedSettings.TileProviderAttribution = HtmlSanitization.SanitizeAttribution(updatedSettings.TileProviderAttribution);

        if (string.IsNullOrWhiteSpace(updatedSettings.TileProviderAttribution))
        {
            ModelState.AddModelError(nameof(ApplicationSettings.TileProviderAttribution), _text["AdminSettings.Validation.Attribution"]);
        }

        if (!TileProviderCatalog.TryValidateTemplate(updatedSettings.TileProviderUrlTemplate, out var templateError))
        {
            ModelState.AddModelError(nameof(ApplicationSettings.TileProviderUrlTemplate), AdminSettingsWebText.TemplateError(_text, templateError));
        }

        if (TileProviderCatalog.RequiresApiKey(updatedSettings.TileProviderUrlTemplate))
        {
            if (string.IsNullOrWhiteSpace(updatedSettings.TileProviderApiKey))
            {
                updatedSettings.TileProviderApiKey = currentSettings.TileProviderApiKey;
            }

            if (string.IsNullOrWhiteSpace(updatedSettings.TileProviderApiKey))
            {
                ModelState.AddModelError(nameof(ApplicationSettings.TileProviderApiKey),
                    _text["AdminSettings.Validation.ApiKey"]);
            }
        }
        else
        {
            updatedSettings.TileProviderApiKey = null;
        }

        return false;
    }

    /// <summary>
    /// Adds tile provider preset metadata needed by the settings view.
    /// </summary>
    private void SetTileProviderViewData()
    {
        ViewData["TileProviderPresets"] = TileProviderCatalog.Presets;
        ViewData["TileProviderCustomKey"] = TileProviderCatalog.CustomProviderKey;
    }
}
