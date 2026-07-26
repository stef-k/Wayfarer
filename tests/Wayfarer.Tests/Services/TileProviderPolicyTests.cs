using System.Net;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Defines the approved Phase 4 provider-policy contract before production wiring.
/// </summary>
public sealed class TileProviderPolicyTests
{
    /// <summary>Supported built-ins migrate to Interactive without consulting dormant values.</summary>
    [Fact]
    public void Resolve_SupportedBuiltInWithDormantValues_IsInteractive()
    {
        var profile = TileProviderPolicyResolver.Resolve(new ApplicationSettings
        {
            TileProviderKey = "opentopomap",
            TileProviderUrlTemplate = "https://a.tile.opentopomap.org/{z}/{x}/{y}.png",
            TileProviderAdvancedLimitsEnabled = true,
            TileOutboundBudgetPerIpPerMinute = 30
        });

        Assert.Equal(TileTrafficMode.Interactive, profile.TrafficMode);
        Assert.False(profile.UsesRateTokens);
        Assert.Equal(0, profile.ClientSeriesPerMinute);
        Assert.Equal(TileProviderCompatibility.Supported, profile.Compatibility.Status);
    }

    /// <summary>Blocked hosts win over a Custom label and advanced traffic controls.</summary>
    [Theory]
    [InlineData("custom", "https://tile.thunderforest.com/cycle/{z}/{x}/{y}.png")]
    [InlineData("unknown", "https://cartodb-basemaps-a.global.ssl.fastly.net/light_all/{z}/{x}/{y}.png")]
    public void Resolve_BlockedEndpoint_PrecedesTrafficMode(string key, string template)
    {
        var profile = TileProviderPolicyResolver.Resolve(new ApplicationSettings
        {
            TileProviderKey = key,
            TileProviderUrlTemplate = template,
            TileProviderAdvancedLimitsEnabled = true
        });

        Assert.Equal(TileProviderCompatibility.Blocked, profile.Compatibility.Status);
        Assert.False(profile.CanContactProvider);
    }

    /// <summary>Blank and unknown provider states remain recoverable but fail closed.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("removed", "https://tiles.example.test/{z}/{x}/{y}.png")]
    public void Resolve_UnknownOrBlankProvider_FailsClosed(string key, string template)
    {
        var profile = TileProviderPolicyResolver.Resolve(new ApplicationSettings
        {
            TileProviderKey = key,
            TileProviderUrlTemplate = template
        });

        Assert.Equal(TileProviderCompatibility.InvalidOrUnsupported, profile.Compatibility.Status);
        Assert.False(profile.CanContactProvider);
    }

    /// <summary>Conservative mode has exact Wayfarer contact and series safeguards.</summary>
    [Fact]
    public void Resolve_Conservative_UsesDocumentedSafeguards()
    {
        var profile = TileProviderPolicyResolver.Resolve(new ApplicationSettings
        {
            TileProviderKey = ApplicationSettings.DefaultTileProviderKey,
            TileProviderUrlTemplate = ApplicationSettings.DefaultTileProviderUrlTemplate,
            TileTrafficMode = TileTrafficMode.Conservative
        });

        Assert.Equal(12, profile.SustainedRequestsPerSecond);
        Assert.Equal(40, profile.BurstCapacity);
        Assert.Equal(8, profile.MaxConcurrency);
        Assert.Equal(480, profile.ClientSeriesPerMinute);
        Assert.True(profile.UsesRateTokens);
    }

    /// <summary>Supplies persisted scalar and relationship violations that runtime resolution must contain.</summary>
    public static TheoryData<Action<ApplicationSettings>> InvalidPersistedProfiles => new()
    {
        settings => settings.TileProviderSustainedRequestsPerSecond = 0,
        settings => settings.TileProviderSustainedRequestsPerSecond = 21,
        settings => settings.TileProviderBurstCapacity = 0,
        settings => settings.TileProviderBurstCapacity = 51,
        settings => settings.TileProviderMaxConcurrency = 0,
        settings => settings.TileProviderMaxConcurrency = 17,
        settings => settings.TileProviderMaxAttempts = 0,
        settings => settings.TileProviderMaxAttempts = 4,
        settings => settings.TileProviderFallbackBaseDelayMs = 249,
        settings => settings.TileProviderFallbackBaseDelayMs = 5001,
        settings => settings.TileProviderFallbackDelayCapSeconds = 0,
        settings => settings.TileProviderFallbackDelayCapSeconds = 31,
        settings => settings.TileProviderMaxIndividualWaitSeconds = 0,
        settings => settings.TileProviderMaxIndividualWaitSeconds = 121,
        settings => settings.TileProviderTotalRetryCeilingSeconds = 4,
        settings => settings.TileProviderTotalRetryCeilingSeconds = 181,
        settings => settings.TileProviderBurstCapacity = 5,
        settings => settings.TileProviderFallbackDelayCapSeconds = 0,
        settings => settings.TileProviderTotalRetryCeilingSeconds = 29
    };

    /// <summary>Invalid persisted values remain recoverable and prevent activation.</summary>
    [Theory]
    [MemberData(nameof(InvalidPersistedProfiles))]
    public void Resolve_InvalidPersistedCustomProfile_ReturnsSafeDefaults(
        Action<ApplicationSettings> invalidate)
    {
        var settings = ValidCustomSettings();
        invalidate(settings);

        var profile = TileProviderPolicyResolver.Resolve(settings);

        Assert.Equal(TileProviderCompatibility.InvalidOrUnsupported, profile.Compatibility.Status);
        Assert.False(profile.CanContactProvider);
    }

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
        Assert.All(profiles, profile =>
        {
            Assert.Equal(TileTrafficMode.Interactive, profile.TrafficMode);
            Assert.False(profile.UsesRateTokens);
            Assert.Equal(TileWorkScheduler.ForegroundConcurrency, profile.MaxConcurrency);
            Assert.Equal(0, profile.ClientSeriesPerMinute);
        });
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

        Assert.Equal(TileTrafficMode.Custom, profile.TrafficMode);
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

    /// <summary>Proves the transport requests HTTP/2 but permits HTTP/1.1 fallback.</summary>
    [Fact]
    public void TileTransport_PrefersHttp2AndPermitsHttp11Fallback()
    {
        using var client = new HttpClient();
        TileHttpTransportConfiguration.Configure(client);
        using var handler = TileHttpTransportConfiguration.CreateHandler();

        Assert.Equal(HttpVersion.Version20, client.DefaultRequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, client.DefaultVersionPolicy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(16, handler.MaxConnectionsPerServer);
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

    private static ApplicationSettings ValidCustomSettings() => new()
    {
        TileProviderKey = TileProviderCatalog.CustomProviderKey,
        TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png",
        TileProviderAdvancedLimitsEnabled = true,
        TileProviderSustainedRequestsPerSecond = 6,
        TileProviderBurstCapacity = 20,
        TileProviderMaxConcurrency = 6,
        TileProviderMaxAttempts = 3,
        TileProviderFallbackBaseDelayMs = 500,
        TileProviderFallbackDelayCapSeconds = 4,
        TileProviderMaxIndividualWaitSeconds = 30,
        TileProviderTotalRetryCeilingSeconds = 45
    };
}

/// <summary>Proves the provider-state table enforces its hard admission boundary.</summary>
[Collection("OutboundBudget")]
public sealed partial class TileProviderStateAdmissionTests
{
    /// <summary>Interactive has no token ceiling while every contact still acquires concurrency.</summary>
    [Fact]
    public async Task AcquireProviderContactAsync_Interactive_AllowsMoreThanHistoricalBurst()
    {
        TileCacheService.OutboundBudget.ResetForTesting();
        var profile = TileProviderPolicyResolver.Resolve(new ApplicationSettings());
        try
        {
            for (var index = 0; index < 81; index++)
            {
                var acquisition = await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                    profile, TileWorkPriority.Foreground, CancellationToken.None);
                Assert.NotNull(acquisition.Lease);
                acquisition.Lease.Dispose();
            }

            Assert.Equal(0, TileCacheService.OutboundBudget.ProviderReplenisherStartCountForTesting);
        }
        finally
        {
            TileCacheService.OutboundBudget.ResetForTesting();
        }
    }

    /// <summary>Shutdown closes provider-state admission before a request can enter the state table.</summary>
    [Fact]
    public async Task AcquireProviderContactAsync_PausedBeforeStateLock_IsRejectedAfterStop()
    {
        TileCacheService.OutboundBudget.ResetForTesting();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TileCacheService.OutboundBudget.ProviderContactLease? retainedLease = null;
        TileCacheService.OutboundBudget.ProviderContactLease? recoveredLease = null;
        TileCacheService.OutboundBudget.SetBeforeProviderStateLockForTesting(
            async (key, cancellationToken) =>
            {
                if (!key.Contains("custom:test-1", StringComparison.Ordinal))
                {
                    return;
                }

                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });

        try
        {
            retainedLease = Assert.IsType<TileCacheService.OutboundBudget.ProviderContactLease>(
                (await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                    Profile(0), TileWorkPriority.Foreground, CancellationToken.None)).Lease);
            var retainedCount = TileCacheService.OutboundBudget.ProviderStateCountForTesting;
            var replenisherStarts = TileCacheService.OutboundBudget.ProviderReplenisherStartCountForTesting;
            var pausedAcquisition = TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                Profile(1), TileWorkPriority.Foreground, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            TileCacheService.OutboundBudget.Stop();
            TileCacheService.OutboundBudget.Stop();
            release.TrySetResult();

            Assert.Null((await pausedAcquisition).Lease);
            Assert.Equal(retainedCount, TileCacheService.OutboundBudget.ProviderStateCountForTesting);
            Assert.Equal(
                replenisherStarts,
                TileCacheService.OutboundBudget.ProviderReplenisherStartCountForTesting);

            retainedLease.Dispose();
            retainedLease = null;
            TileCacheService.OutboundBudget.ResetForTesting();
            recoveredLease = Assert.IsType<TileCacheService.OutboundBudget.ProviderContactLease>(
                (await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                    Profile(2), TileWorkPriority.Foreground, CancellationToken.None)).Lease);
        }
        finally
        {
            release.TrySetResult();
            retainedLease?.Dispose();
            recoveredLease?.Dispose();
            TileCacheService.OutboundBudget.ResetForTesting();
        }
    }

    /// <summary>A state referenced before capacity completion cannot be retired or duplicated.</summary>
    [Fact]
    public async Task AcquireProviderContactAsync_ReferencedWaiter_RemainsAuthoritativeUnderPressure()
    {
        TileCacheService.OutboundBudget.ResetForTesting();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = new List<TileCacheService.OutboundBudget.ProviderContactLease>();
        TileCacheService.OutboundBudget.SetBeforeProviderCapacityWaitForTesting(
            async (key, cancellationToken) =>
            {
                if (!key.Contains("custom:test-0", StringComparison.Ordinal))
                {
                    return;
                }

                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });

        try
        {
            var waiting = TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                Profile(0), TileWorkPriority.Foreground, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var index = 1; index < 32; index++)
            {
                leases.Add(Assert.IsType<TileCacheService.OutboundBudget.ProviderContactLease>(
                    (await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                        Profile(index), TileWorkPriority.Foreground, CancellationToken.None)).Lease));
            }

            Assert.Null((await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                Profile(32), TileWorkPriority.Foreground, CancellationToken.None)).Lease);
            Assert.Equal(32, TileCacheService.OutboundBudget.ProviderStateCountForTesting);

            release.TrySetResult();
            leases.Add(Assert.IsType<TileCacheService.OutboundBudget.ProviderContactLease>(
                (await waiting).Lease));
            Assert.Equal(32, TileCacheService.OutboundBudget.ProviderStateCountForTesting);
        }
        finally
        {
            release.TrySetResult();
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
            TileCacheService.OutboundBudget.ResetForTesting();
        }
    }

    /// <summary>A referenced table at capacity rejects a distinct state until one owner releases.</summary>
    [Fact]
    public async Task AcquireProviderContactAsync_AtThirtyTwoReferencedStates_RejectsThenRecovers()
    {
        TileCacheService.OutboundBudget.ResetForTesting();
        var leases = new List<TileCacheService.OutboundBudget.ProviderContactLease>();
        try
        {
            for (var index = 0; index < 32; index++)
            {
                var lease = await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                    Profile(index), TileWorkPriority.Foreground, CancellationToken.None);
                leases.Add(Assert.IsType<TileCacheService.OutboundBudget.ProviderContactLease>(lease.Lease));
            }

            var rejected = await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                Profile(32), TileWorkPriority.Foreground, CancellationToken.None);

            Assert.Null(rejected.Lease);
            Assert.Equal(32, TileCacheService.OutboundBudget.ProviderStateCountForTesting);

            leases[0].Dispose();
            leases.RemoveAt(0);
            var admitted = await TileCacheService.OutboundBudget.AcquireProviderContactAsync(
                Profile(33), TileWorkPriority.Foreground, CancellationToken.None);
            leases.Add(Assert.IsType<TileCacheService.OutboundBudget.ProviderContactLease>(admitted.Lease));
            Assert.True(TileCacheService.OutboundBudget.ProviderStateCountForTesting <= 32);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
            TileCacheService.OutboundBudget.ResetForTesting();
        }
    }

    private static TileProviderPolicy Profile(int index) => new(
        $"custom:test-{index}", TileTrafficMode.Custom,
        new(TileProviderCompatibility.Supported, "test", "test", "test"), true,
        20, 50, 16, 0, 3,
        TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), false);
}
