using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Contains focused Admin regressions for issue #396 provider recovery and audit behavior.</summary>
public partial class AdminSettingsControllerTests
{
    /// <summary>Unsupported persisted provider state survives an unrelated normal form save.</summary>
    [Theory]
    [InlineData("thunderforest-cycle", "https://tile.thunderforest.com/cycle/{z}/{x}/{y}.png")]
    [InlineData("carto-dark", "https://cartodb-basemaps-a.global.ssl.fastly.net/dark_all/{z}/{x}/{y}.png")]
    [InlineData("retired-provider", "https://tiles.retired.example/{z}/{x}/{y}.png")]
    [InlineData("", "")]
    [InlineData("custom", "malformed endpoint")]
    public async Task Update_UnrelatedSavePreservesUnsupportedProviderRecoveryState(
        string providerKey, string template)
    {
        const string attribution = "Recovery attribution";
        const string apiKey = "recovery-secret";
        var db = CreateDbContext();
        var existing = new ApplicationSettings
        {
            Id = 1,
            TileProviderKey = providerKey,
            TileProviderUrlTemplate = template,
            TileProviderAttribution = attribution,
            TileProviderApiKey = apiKey,
            TileProviderSustainedRequestsPerSecond = 17,
            TileProviderBurstCapacity = 41,
            TileProviderMaxConcurrency = 9,
            TileOutboundBudgetPerIpPerMinute = 321,
            TileProviderMaxAttempts = 2,
            TileProviderFallbackBaseDelayMs = 750,
            TileProviderFallbackDelayCapSeconds = 7,
            TileProviderMaxIndividualWaitSeconds = 44,
            TileProviderTotalRetryCeilingSeconds = 88,
            IsRegistrationOpen = false
        };
        db.ApplicationSettings.Add(existing);
        await db.SaveChangesAsync();
        TileProviderCacheIdentity? originalIdentity = Uri.TryCreate(template, UriKind.Absolute, out _)
            ? TileProviderCatalog.CreateCacheIdentity(providerKey, template)
            : null;
        var (controller, _, _) = BuildController(db);

        var result = await controller.Update(new ApplicationSettings
        {
            Id = 1,
            IsRegistrationOpen = true,
            TileProviderKey = providerKey
        });

        Assert.IsType<RedirectToActionResult>(result);
        var stored = Assert.IsType<ApplicationSettings>(await db.ApplicationSettings.FindAsync(1));
        Assert.Equal(providerKey, stored.TileProviderKey);
        Assert.Equal(template, stored.TileProviderUrlTemplate);
        Assert.Equal(attribution, stored.TileProviderAttribution);
        Assert.Equal(apiKey, stored.TileProviderApiKey);
        Assert.Equal(17, stored.TileProviderSustainedRequestsPerSecond);
        Assert.Equal(41, stored.TileProviderBurstCapacity);
        Assert.Equal(9, stored.TileProviderMaxConcurrency);
        Assert.Equal(321, stored.TileOutboundBudgetPerIpPerMinute);
        Assert.Equal(2, stored.TileProviderMaxAttempts);
        Assert.Equal(750, stored.TileProviderFallbackBaseDelayMs);
        Assert.Equal(7, stored.TileProviderFallbackDelayCapSeconds);
        Assert.Equal(44, stored.TileProviderMaxIndividualWaitSeconds);
        Assert.Equal(88, stored.TileProviderTotalRetryCeilingSeconds);
        if (originalIdentity != null)
        {
            Assert.Equal(originalIdentity, TileProviderCatalog.CreateCacheIdentity(
                stored.TileProviderKey, stored.TileProviderUrlTemplate));
        }
    }

    /// <summary>A blocked provider marks preserved retry controls inactive in one safe audit transition.</summary>
    [Fact]
    public async Task Update_BlockedProviderAuditsPreservedRetryControlsAsInactive()
    {
        var db = CreateDbContext();
        var existing = CreateAuditSettings(
            "thunderforest-cycle",
            "https://tile.thunderforest.com/cycle/{z}/{x}/{y}.png");
        db.ApplicationSettings.Add(existing);
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);

        await controller.Update(CreateSupportedAuditUpdate(TileTrafficMode.Conservative));

        var audit = Assert.Single(db.AuditLogs, entry => entry.Action == "SettingsUpdate");
        Assert.Contains("Compatibility=Blocked", audit.Details);
        Assert.Contains("TileRetryControlsActive=False", audit.Details);
        AssertPreservedRetryValuesAreBoundedAndNotEffective(audit.Details);
        AssertSafeFocusedAudit(audit.Details, existing);
        AssertStoredRetryValuesUnchanged(await db.ApplicationSettings.FindAsync(1));
    }

    /// <summary>An invalid Custom profile preserves correction values while marking retry controls inactive.</summary>
    [Fact]
    public async Task Update_InvalidCustomProviderAuditsPreservedRetryControlsAsInactive()
    {
        var db = CreateDbContext();
        var existing = CreateAuditSettings(
            TileProviderCatalog.CustomProviderKey,
            "https://tiles.example.test/{z}/{x}/{y}.png");
        existing.TileProviderAdvancedLimitsEnabled = true;
        existing.TileProviderMaxAttempts = 4;
        db.ApplicationSettings.Add(existing);
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);

        await controller.Update(CreateSupportedAuditUpdate(TileTrafficMode.Interactive));

        var audit = Assert.Single(db.AuditLogs, entry => entry.Action == "SettingsUpdate");
        Assert.Contains("Compatibility=InvalidOrUnsupported", audit.Details);
        Assert.Contains("TileRetryControlsActive=False", audit.Details);
        Assert.Contains("TileRetryMaxAttempts=4", audit.Details);
        Assert.DoesNotContain("TileEffectiveMaxAttempts=4", audit.Details);
        AssertSafeFocusedAudit(audit.Details, existing);
        var stored = Assert.IsType<ApplicationSettings>(await db.ApplicationSettings.FindAsync(1));
        Assert.Equal(4, stored.TileProviderMaxAttempts);
        AssertStoredRetryValuesUnchanged(stored);
    }

    /// <summary>An active Conservative policy reports its bounded retry controls as active and accurate.</summary>
    [Fact]
    public async Task Update_ActivePolicyAuditsEffectiveRetryControlsAsActive()
    {
        var db = CreateDbContext();
        var existing = CreateAuditSettings(
            ApplicationSettings.DefaultTileProviderKey,
            ApplicationSettings.DefaultTileProviderUrlTemplate);
        db.ApplicationSettings.Add(existing);
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);

        await controller.Update(CreateSupportedAuditUpdate(TileTrafficMode.Conservative));

        var audit = Assert.Single(db.AuditLogs, entry => entry.Action == "SettingsUpdate");
        Assert.Contains("Compatibility=Supported", audit.Details);
        Assert.Contains("TileRetryControlsActive=True", audit.Details);
        Assert.Contains("TileRetryMaxAttempts=2", audit.Details);
        Assert.Contains("TileRetryFallbackBaseDelayMs=750", audit.Details);
        Assert.Contains("TileRetryFallbackDelayCapSeconds=7", audit.Details);
        Assert.Contains("TileRetryMaxIndividualWaitSeconds=44", audit.Details);
        Assert.Contains("TileRetryTotalCeilingSeconds=88", audit.Details);
        AssertSafeFocusedAudit(audit.Details, existing);
        AssertStoredRetryValuesUnchanged(await db.ApplicationSettings.FindAsync(1));
    }

    /// <summary>Creates settings with distinctive bounded retry values for audit assertions.</summary>
    private static ApplicationSettings CreateAuditSettings(string providerKey, string template) => new()
    {
        Id = 1,
        TileProviderKey = providerKey,
        TileProviderUrlTemplate = template,
        TileProviderAttribution = "audit-attribution-marker",
        TileProviderApiKey = "audit-api-key-marker",
        TileTrafficMode = TileTrafficMode.Interactive,
        TileProviderMaxAttempts = 2,
        TileProviderFallbackBaseDelayMs = 750,
        TileProviderFallbackDelayCapSeconds = 7,
        TileProviderMaxIndividualWaitSeconds = 44,
        TileProviderTotalRetryCeilingSeconds = 88
    };

    /// <summary>Creates the explicitly submitted supported-provider transition.</summary>
    private static ApplicationSettings CreateSupportedAuditUpdate(TileTrafficMode mode) => new()
    {
        Id = 1,
        TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
        TileProviderUrlTemplate = ApplicationSettings.DefaultTileProviderUrlTemplate,
        TileProviderAttribution = ApplicationSettings.DefaultTileProviderAttribution,
        TileTrafficMode = mode,
        TileProviderMaxAttempts = 2,
        TileProviderFallbackBaseDelayMs = 750,
        TileProviderFallbackDelayCapSeconds = 7,
        TileProviderMaxIndividualWaitSeconds = 44,
        TileProviderTotalRetryCeilingSeconds = 88
    };

    /// <summary>Asserts inactive retry values remain bounded without effective labels.</summary>
    private static void AssertPreservedRetryValuesAreBoundedAndNotEffective(string details)
    {
        Assert.Contains("TileRetryMaxAttempts=2", details);
        Assert.Contains("TileRetryFallbackBaseDelayMs=750", details);
        Assert.Contains("TileRetryFallbackDelayCapSeconds=7", details);
        Assert.Contains("TileRetryMaxIndividualWaitSeconds=44", details);
        Assert.Contains("TileRetryTotalCeilingSeconds=88", details);
        Assert.DoesNotContain("TileEffectiveMaxAttempts", details);
        Assert.DoesNotContain("TileEffectiveFallbackBaseDelayMs", details);
        Assert.DoesNotContain("TileEffectiveFallbackDelayCapSeconds", details);
        Assert.DoesNotContain("TileEffectiveMaxIndividualWaitSeconds", details);
        Assert.DoesNotContain("TileEffectiveTotalRetryCeilingSeconds", details);
    }

    /// <summary>Asserts the audit excludes provider secrets, identity data, and settings snapshots.</summary>
    private static void AssertSafeFocusedAudit(string details, ApplicationSettings original)
    {
        Assert.DoesNotContain(original.TileProviderUrlTemplate, details, StringComparison.Ordinal);
        Assert.DoesNotContain(original.TileProviderAttribution, details, StringComparison.Ordinal);
        Assert.DoesNotContain(original.TileProviderApiKey!, details, StringComparison.Ordinal);
        Assert.DoesNotContain("TileProviderUrlTemplate", details);
        Assert.DoesNotContain("TileProviderApiKey", details);
        Assert.DoesNotContain("Credential", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserId", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IpAddress", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApplicationSettings", details);
    }

    /// <summary>Asserts persistence did not mutate the submitted retry values.</summary>
    private static void AssertStoredRetryValuesUnchanged(ApplicationSettings? stored)
    {
        var settings = Assert.IsType<ApplicationSettings>(stored);
        Assert.Equal(2, settings.TileProviderMaxAttempts);
        Assert.Equal(750, settings.TileProviderFallbackBaseDelayMs);
        Assert.Equal(7, settings.TileProviderFallbackDelayCapSeconds);
        Assert.Equal(44, settings.TileProviderMaxIndividualWaitSeconds);
        Assert.Equal(88, settings.TileProviderTotalRetryCeilingSeconds);
    }
}
