using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Verifies stable tile-pipeline diagnostics and durable provider-policy behavior.
/// </summary>
[Collection("OutboundBudget")]
public sealed class TileCacheDiagnosticsAndPolicyTests
{
    /// <summary>Proves a fresh local tile emits a stable hit event and performs no upstream work.</summary>
    [Fact]
    public async Task FreshCacheHit_EmitsStableDiagnostic_WithoutUpstreamRequest()
    {
        using var harness = new TileCacheTestHarness();
        using var scope = harness.CreateScope();
        SetHttpContext(scope);
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        Assert.True(await service.CacheTileAsync(CanonicalTileUrl(5, 1, 2), "5", "1", "2"));
        var upstreamCallsAfterFill = harness.Upstream.Requests.Count;

        var result = await service.RetrieveTileAsync("5", "1", "2", CanonicalTileUrl(5, 1, 2));

        Assert.NotNull(result.TileData);
        Assert.Equal(upstreamCallsAfterFill, harness.Upstream.Requests.Count);
        var diagnostic = AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.FreshCacheHit);
        Assert.Equal("fresh", diagnostic.Fields["CacheOutcome"]);
    }

    /// <summary>Proves stale bytes return immediately while refresh scheduling is observable and coalesced.</summary>
    [Fact]
    public async Task StaleCacheHit_ReturnsBeforeRefreshAndReportsScheduledThenCoalesced()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingTileHandler(async (request, cancellationToken) =>
        {
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                refreshStarted.TrySetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NotModified)
                {
                    Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) } }
                };
            }

            var response = PngResponse(maxAge: TimeSpan.Zero);
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"stale-v1\"");
            return response;
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        SetHttpContext(scope);
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        var tileUrl = CanonicalTileUrl(5, 3, 4);
        Assert.True(await service.CacheTileAsync(tileUrl, "5", "3", "4"));
        await File.WriteAllTextAsync(
            Path.Combine(harness.CacheDirectory, "5_3_4.png.meta"),
            JsonSerializer.Serialize(new TileSidecarMetadata
            {
                ETag = "\"stale-v1\"",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
            }));
        TileCacheService.ResetStaticStateForTesting();

        var first = await service.RetrieveTileAsync("5", "3", "4", tileUrl);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await service.RetrieveTileAsync("5", "3", "4", tileUrl);

        Assert.NotNull(first.TileData);
        Assert.NotNull(second.TileData);
        Assert.Contains(harness.Logs.Entries,
            entry => entry.EventId.Id == (int)TileCacheDiagnosticEventIds.StaleCacheHit);
        AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.StaleRefreshScheduled);
        AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.StaleRefreshCoalesced);

        releaseRefresh.TrySetResult();
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync(
            "5_3_4",
            TimeSpan.FromSeconds(2)));
    }

    /// <summary>Proves one canonical OSM request emits neither cache busting nor speculative prefetch traffic.</summary>
    [Fact]
    public async Task CanonicalOsmRequest_EmitsNoCacheBustingOrPrefetchTraffic()
    {
        using var harness = new TileCacheTestHarness();
        using var scope = harness.CreateScope();
        SetHttpContext(scope);
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        var requestedUrl = CanonicalTileUrl(9, 120, 85);

        var result = await service.RetrieveTileAsync("9", "120", "85", requestedUrl);

        Assert.NotNull(result.TileData);
        var request = Assert.Single(harness.Upstream.Requests);
        Assert.Equal(requestedUrl, request.RequestUri?.ToString());
        Assert.DoesNotContain("Cache-Control", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pragma", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.ColdCacheMiss);
        AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.UpstreamAttempt);
        AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.UpstreamStatus);
        AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.CacheWriteOutcome);
    }

    /// <summary>Proves provider credentials are absent from messages, structured fields, and exceptions.</summary>
    [Fact]
    public async Task ProviderSecret_DoesNotAppearInDiagnosticOutput()
    {
        const string secret = "phase1-super-secret";
        var upstream = new RecordingTileHandler((_, _) =>
            throw new HttpRequestException($"Fake provider failure contained {secret}."));
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        SetHttpContext(scope);
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        var result = await service.RetrieveTileAsync(
            "5",
            "7",
            "8",
            $"https://tiles.example.test/5/7/8.png?apiKey={secret}");

        Assert.Null(result.TileData);
        Assert.NotEmpty(harness.Logs.Entries);
        Assert.DoesNotContain(harness.Logs.Entries, entry =>
            ContainsSecret(entry.Message, secret) ||
            entry.Fields.Values.Any(value => ContainsSecret(value?.ToString(), secret)) ||
            ContainsSecret(entry.Exception?.ToString(), secret));
    }

    /// <summary>Proves global rejection includes a stable scope and deterministic budget-wait duration.</summary>
    [Fact]
    public async Task GlobalBudgetRejection_HasStableScopeAndWaitFields()
    {
        using var harness = new TileCacheTestHarness();
        TileCacheService.OutboundBudget.SetAcquireOverrideForTesting(_ =>
            Task.FromResult(new OutboundBudgetAcquisition(
                Acquired: false,
                WaitDuration: TileCacheService.OutboundBudget.AcquireTimeout)));
        using var scope = harness.CreateScope();
        SetHttpContext(scope);
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        var result = await service.RetrieveTileAsync("5", "9", "10", CanonicalTileUrl(5, 9, 10));

        Assert.True(result.BudgetExhausted);
        var diagnostic = AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.GlobalBudgetRejected);
        Assert.Equal("global", diagnostic.Fields["BudgetScope"]);
        Assert.Equal(3500d, Convert.ToDouble(diagnostic.Fields["WaitMilliseconds"]));
        Assert.Empty(harness.Upstream.Requests);
    }

    /// <summary>Proves the per-client allowance is distinguishable from the global outbound budget.</summary>
    [Fact]
    public async Task PerClientBudgetRejection_IsDistinctFromGlobalRejection()
    {
        using var harness = new TileCacheTestHarness();
        harness.Settings.TileOutboundBudgetPerIpPerMinute = 1;

        using (var firstScope = harness.CreateScope())
        {
            SetHttpContext(firstScope);
            var firstService = firstScope.ServiceProvider.GetRequiredService<TileCacheService>();
            Assert.NotNull((await firstService.RetrieveTileAsync(
                "5", "11", "12", CanonicalTileUrl(5, 11, 12))).TileData);
        }

        using (var secondScope = harness.CreateScope())
        {
            SetHttpContext(secondScope);
            var secondService = secondScope.ServiceProvider.GetRequiredService<TileCacheService>();
            var rejected = await secondService.RetrieveTileAsync(
                "5", "11", "13", CanonicalTileUrl(5, 11, 13));
            Assert.True(rejected.BudgetExhausted);
        }

        var diagnostic = AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.ClientBudgetRejected);
        Assert.Equal("outbound-client", diagnostic.Fields["BudgetScope"]);
        Assert.DoesNotContain(harness.Logs.Entries,
            entry => entry.EventId.Id == (int)TileCacheDiagnosticEventIds.GlobalBudgetRejected);
        Assert.Single(harness.Upstream.Requests);
    }

    /// <summary>Proves current attempt numbering and fixed retry delay are observable without changing policy.</summary>
    [Fact]
    public async Task CurrentRetryDelay_IsReportedWithoutChangingRetryPolicy()
    {
        var upstream = new RecordingTileHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var harness = new TileCacheTestHarness(upstream);
        TileCacheService.SetColdMissRetryDelayForTesting((_, _) => Task.CompletedTask);
        using var scope = harness.CreateScope();
        SetHttpContext(scope);
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        var result = await service.RetrieveTileAsync("5", "16", "17", CanonicalTileUrl(5, 16, 17));

        Assert.Null(result.TileData);
        Assert.Equal(3, harness.Upstream.Requests.Count);
        var attempts = harness.Logs.Entries
            .Where(entry => entry.EventId.Id == (int)TileCacheDiagnosticEventIds.UpstreamAttempt)
            .Select(entry => Convert.ToInt32(entry.Fields["AttemptNumber"]))
            .ToArray();
        Assert.Equal([1, 2, 3], attempts);
        var delays = harness.Logs.Entries
            .Where(entry => entry.EventId.Id == (int)TileCacheDiagnosticEventIds.RetryDelaySelected)
            .ToArray();
        Assert.Equal(2, delays.Length);
        Assert.All(delays, delay =>
            Assert.Equal(500d, Convert.ToDouble(delay.Fields["RetryDelayMilliseconds"])));
    }

    /// <summary>Assigns the same-origin request context used by one scoped service.</summary>
    private static void SetHttpContext(IServiceScope scope)
    {
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            TileCacheTestHarness.CreateHttpContext();
    }

    /// <summary>Returns the only diagnostic with the requested stable identifier.</summary>
    private static TestLogProvider.TestLogEntry AssertDiagnostic(
        TestLogProvider logs,
        TileCacheDiagnosticEventIds eventId) =>
        Assert.Single(logs.Entries, entry => entry.EventId.Id == (int)eventId);

    /// <summary>Builds one canonical OSM Standard tile URL for the intercepting fake provider.</summary>
    private static string CanonicalTileUrl(int zoom, int x, int y) =>
        $"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png";

    /// <summary>Builds a deterministic cacheable PNG response.</summary>
    private static HttpResponseMessage PngResponse(TimeSpan maxAge)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = maxAge };
        return response;
    }

    /// <summary>Checks diagnostic text for a forbidden provider secret using ordinal comparison.</summary>
    private static bool ContainsSecret(string? value, string secret) =>
        value?.Contains(secret, StringComparison.Ordinal) == true;
}
