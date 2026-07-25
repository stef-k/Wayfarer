using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Defines the approved Phase 4 provider-policy contract before production wiring.
/// </summary>
public sealed class TileProviderPolicyTests
{
    /// <summary>
    /// Ensures every built-in preset has its own immutable identity and approved defaults.
    /// </summary>
    [Fact]
    public void Resolve_BuiltInPresets_UsesDistinctApprovedProfiles()
    {
        var profiles = TileProviderCatalog.Presets
            .Select(preset => TileProviderPolicyResolver.Resolve(new ApplicationSettings
            {
                TileProviderKey = preset.Key,
                TileProviderUrlTemplate = preset.UrlTemplate
            }))
            .ToArray();

        Assert.Equal(profiles.Length, profiles.Select(profile => profile.Identity).Distinct().Count());
        Assert.All(profiles, AssertApprovedDefaults);
    }

    /// <summary>
    /// Ensures relabeling the canonical OSM endpoint cannot bypass its fixed no-prefetch profile.
    /// </summary>
    [Fact]
    public void Resolve_CustomLabelWithCanonicalOsmTemplate_UsesOsmProfile()
    {
        var profile = TileProviderPolicyResolver.Resolve(new ApplicationSettings
        {
            TileProviderKey = TileProviderCatalog.CustomProviderKey,
            TileProviderUrlTemplate = ApplicationSettings.DefaultTileProviderUrlTemplate,
            TileProviderAdvancedLimitsEnabled = true,
            TileProviderSustainedRequestsPerSecond = 20,
            TileProviderBurstCapacity = 50,
            TileProviderMaxConcurrency = 16
        });

        Assert.Equal("builtin:osm", profile.Identity);
        AssertApprovedDefaults(profile);
        Assert.False(profile.PrefetchEnabled);
    }

    /// <summary>
    /// Ensures custom limits resolve only when advanced mode is explicitly enabled.
    /// </summary>
    [Fact]
    public void Resolve_CustomAdvanced_UsesConfiguredBoundedValues()
    {
        var profile = TileProviderPolicyResolver.Resolve(new ApplicationSettings
        {
            TileProviderKey = TileProviderCatalog.CustomProviderKey,
            TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png",
            TileProviderAdvancedLimitsEnabled = true,
            TileProviderSustainedRequestsPerSecond = 20,
            TileProviderBurstCapacity = 50,
            TileProviderMaxConcurrency = 16,
            TileProviderMaxAttempts = 1,
            TileProviderFallbackBaseDelayMs = 250,
            TileProviderFallbackDelayCapSeconds = 30,
            TileProviderMaxIndividualWaitSeconds = 120,
            TileProviderTotalRetryCeilingSeconds = 180
        });

        Assert.Equal(20, profile.SustainedRequestsPerSecond);
        Assert.Equal(50, profile.BurstCapacity);
        Assert.Equal(16, profile.MaxConcurrency);
        Assert.Equal(1, profile.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(250), profile.FallbackBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), profile.FallbackDelayCap);
        Assert.Equal(TimeSpan.FromSeconds(120), profile.MaxIndividualWait);
        Assert.Equal(TimeSpan.FromSeconds(180), profile.TotalRetryCeiling);
        Assert.False(profile.PrefetchEnabled);
    }

    private static void AssertApprovedDefaults(TileProviderPolicy profile)
    {
        Assert.Equal(6, profile.SustainedRequestsPerSecond);
        Assert.Equal(20, profile.BurstCapacity);
        Assert.Equal(6, profile.MaxConcurrency);
        Assert.Equal(3, profile.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(500), profile.FallbackBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(4), profile.FallbackDelayCap);
        Assert.Equal(TimeSpan.FromSeconds(30), profile.MaxIndividualWait);
        Assert.Equal(TimeSpan.FromSeconds(45), profile.TotalRetryCeiling);
        Assert.False(profile.PrefetchEnabled);
    }
}
