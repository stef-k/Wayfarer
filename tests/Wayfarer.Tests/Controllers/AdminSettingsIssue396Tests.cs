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

    /// <summary>A successful policy save emits one complete bounded non-secret transition.</summary>
    [Fact]
    public async Task Update_TrafficModeAuditsCompleteEffectivePolicyOnce()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        await db.SaveChangesAsync();
        var (controller, _, _) = BuildController(db);
        var updated = new ApplicationSettings
        {
            Id = 1,
            TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
            TileProviderUrlTemplate = ApplicationSettings.DefaultTileProviderUrlTemplate,
            TileProviderAttribution = ApplicationSettings.DefaultTileProviderAttribution,
            TileTrafficMode = TileTrafficMode.Conservative
        };

        await controller.Update(updated);

        var audit = Assert.Single(db.AuditLogs, entry => entry.Action == "SettingsUpdate");
        Assert.Contains("TileEffectiveRateActive", audit.Details);
        Assert.Contains("TileEffectiveBurstActive", audit.Details);
        Assert.Contains("TileEffectiveConcurrencyActive", audit.Details);
        Assert.Contains("TileEffectiveClientAllowanceActive", audit.Details);
        Assert.Contains("TileEffectiveMaxAttempts", audit.Details);
        Assert.Contains("TileEffectiveFallbackBaseDelayMs", audit.Details);
        Assert.Contains("TileEffectiveFallbackDelayCapSeconds", audit.Details);
        Assert.Contains("TileEffectiveMaxIndividualWaitSeconds", audit.Details);
        Assert.Contains("TileEffectiveTotalRetryCeilingSeconds", audit.Details);
        Assert.DoesNotContain(updated.TileProviderUrlTemplate, audit.Details, StringComparison.Ordinal);
    }
}
