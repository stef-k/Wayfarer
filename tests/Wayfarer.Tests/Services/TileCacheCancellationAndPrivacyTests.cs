using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Verifies cancellation classification, throttle scope, and redirect privacy for Phase 1 diagnostics.
/// </summary>
[Collection("OutboundBudget")]
public sealed class TileCacheCancellationAndPrivacyTests
{
    /// <summary>Proves caller cancellation is the only transport path classified as cancellation.</summary>
    [Fact]
    public async Task CallerCancelledTransport_EmitsCancellationOnly()
    {
        using var cancellation = new CancellationTokenSource();
        var transportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingTileHandler(async (_, token) =>
        {
            transportStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The controlled transport unexpectedly completed.");
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        SetHttpContext(scope, TileCacheTestHarness.CreateHttpContext(cancellation.Token));
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        // Cancel only after transport starts, then drain the scheduler-owned runner before reading logs.
        var retrieval = service.RetrieveTileAsync(
            "5", "18", "19", CanonicalTileUrl(5, 18, 19), cancellation.Token);
        await transportStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            retrieval);
        await TileWorkScheduler.StopAndDrainAsync();

        AssertCancellation(harness.Logs, "upstream-transport");
        AssertNoUpstreamFailure(harness.Logs);
        Assert.Single(harness.Upstream.Requests);
    }

    /// <summary>Proves an uncancelled transport timeout is classified as upstream failure.</summary>
    [Fact]
    public async Task UncancelledTransportTimeout_EmitsUpstreamFailureOnly()
    {
        var upstream = new RecordingTileHandler((_, token) =>
            throw new OperationCanceledException(token));
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        SetHttpContext(scope, TileCacheTestHarness.CreateHttpContext());
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        var result = await service.RetrieveTileAsync("5", "18", "20", CanonicalTileUrl(5, 18, 20));

        Assert.Null(result.TileData);
        AssertNoCancellation(harness.Logs);
        Assert.Equal(3, harness.Logs.Entries.Count(
            entry => entry.EventId.Id == (int)TileCacheDiagnosticEventIds.UpstreamFailure));
        Assert.Equal(3, harness.Upstream.Requests.Count);
    }

    /// <summary>Proves cancellation while waiting for global capacity has its bounded stage.</summary>
    [Fact]
    public async Task GlobalBudgetWaitCancellation_StopsBeforeUpstream()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new TileCacheTestHarness();
        TileCacheService.OutboundBudget.SetAcquireOverrideForTesting(token =>
        {
            cancellation.Cancel();
            return WaitForSharedCancellationAsync<OutboundBudgetAcquisition>(token);
        });
        using var scope = harness.CreateScope();
        SetHttpContext(scope, TileCacheTestHarness.CreateHttpContext(cancellation.Token));
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RetrieveTileAsync(
                "5", "18", "21", CanonicalTileUrl(5, 18, 21), cancellation.Token));
        await TileWorkScheduler.StopAndDrainAsync();

        AssertCancellation(harness.Logs, "global-budget-wait");
        AssertNoUpstreamFailure(harness.Logs);
        Assert.Empty(harness.Upstream.Requests);
    }

    /// <summary>Proves cancellation during the fixed cold-miss retry delay stops further attempts.</summary>
    [Fact]
    public async Task ColdMissRetryDelayCancellation_StopsFurtherAttempts()
    {
        using var cancellation = new CancellationTokenSource();
        var retryDelayStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingTileHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var harness = new TileCacheTestHarness(upstream);
        TileCacheService.SetColdMissRetryDelayForTesting(async (_, token) =>
        {
            retryDelayStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        using var scope = harness.CreateScope();
        SetHttpContext(scope, TileCacheTestHarness.CreateHttpContext(cancellation.Token));
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        // Enter the retry delay before cancelling, then drain its shared runner before reading logs.
        var retrieval = service.RetrieveTileAsync(
            "5", "18", "22", CanonicalTileUrl(5, 18, 22), cancellation.Token);
        await retryDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            retrieval);
        await TileWorkScheduler.StopAndDrainAsync();

        AssertCancellation(harness.Logs, "cold-miss-retry-delay");
        AssertNoUpstreamFailure(harness.Logs);
        Assert.Single(harness.Upstream.Requests);
    }

    /// <summary>Waits until the scheduler-owned token reflects the last waiter's cancellation.</summary>
    private static async Task<T> WaitForSharedCancellationAsync<T>(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("The shared cancellation wait unexpectedly completed.");
    }

    /// <summary>Proves stale-refresh delay cancellation is stage-specific and removes series state.</summary>
    [Fact]
    public async Task StaleRefreshDelayCancellation_RemovesRefreshState()
    {
        var conditionalAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingTileHandler((request, _) =>
        {
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                conditionalAttempted.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(ExpiredPngResponse());
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        SetHttpContext(scope, TileCacheTestHarness.CreateHttpContext());
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        await SeedExpiredTileAsync(service, harness.CacheDirectory, 5, 18, 23);
        TileCacheService.SetRefreshRetryDelayForTesting(_ => TimeSpan.FromMinutes(1));

        var result = await service.RetrieveTileAsync("5", "18", "23", CanonicalTileUrl(5, 18, 23));
        await conditionalAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        TileCacheService.CancelRefreshForTesting("5_18_23");

        Assert.NotNull(result.TileData);
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync(
            "5_18_23", TimeSpan.FromSeconds(2)));
        AssertCancellation(harness.Logs, "stale-refresh-delay");
        AssertNoUpstreamFailure(harness.Logs);
        Assert.Equal(2, harness.Upstream.Requests.Count);
    }

    /// <summary>Proves cancellation of the final stale attempt reaches the outer boundary and stops.</summary>
    [Fact]
    public async Task StaleRefreshFinalAttemptCancellation_RemovesRefreshState()
    {
        var finalAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conditionalAttempts = 0;
        var upstream = new RecordingTileHandler(async (request, token) =>
        {
            if (request.Headers.IfNoneMatch.Count == 0)
            {
                return ExpiredPngResponse();
            }

            if (Interlocked.Increment(ref conditionalAttempts) < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            finalAttemptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The cancelled final attempt must not continue.");
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        SetHttpContext(scope, TileCacheTestHarness.CreateHttpContext());
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
        await SeedExpiredTileAsync(service, harness.CacheDirectory, 5, 18, 24);
        TileCacheService.SetRefreshRetryDelayForTesting(_ => TimeSpan.Zero);

        var result = await service.RetrieveTileAsync("5", "18", "24", CanonicalTileUrl(5, 18, 24));
        await finalAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        TileCacheService.CancelRefreshForTesting("5_18_24");

        Assert.NotNull(result.TileData);
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync(
            "5_18_24", TimeSpan.FromSeconds(2)));
        AssertCancellation(harness.Logs, "stale-refresh-attempt");
        AssertNoUpstreamFailure(harness.Logs);
        Assert.Equal(3, conditionalAttempts);
    }

    /// <summary>Proves request rate-limit diagnostics distinguish authenticated and anonymous gates.</summary>
    [Theory]
    [InlineData(true, "request-authenticated")]
    [InlineData(false, "request-anonymous")]
    public async Task RequestThrottle_Retains429AndReportsBoundedScope(
        bool authenticated,
        string expectedScope)
    {
        using var harness = new TileCacheTestHarness();
        harness.Settings.TileRateLimitEnabled = true;
        harness.Settings.TileRateLimitPerMinute = 1;
        harness.Settings.TileRateLimitAuthenticatedPerMinute = 1;
        using var scope = harness.CreateScope();
        var context = TileCacheTestHarness.CreateHttpContext();
        if (authenticated)
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "phase1-user")],
                "Phase1Test");
            context.User = new ClaimsPrincipal(identity);
        }
        var controller = CreateController(scope, context);

        Assert.IsType<FileContentResult>(await controller.GetTile(5, 25, 1));
        var rejected = Assert.IsType<ObjectResult>(await controller.GetTile(5, 26, 1));

        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.StatusCode);
        var diagnostic = AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.ClientBudgetRejected);
        Assert.Equal(expectedScope, diagnostic.Fields["BudgetScope"]);
    }

    /// <summary>Proves the outbound-client gate retains the controller's 503 response and retry header.</summary>
    [Fact]
    public async Task OutboundClientThrottle_Retains503AndReportsBoundedScope()
    {
        using var harness = new TileCacheTestHarness();
        harness.Settings.TileOutboundBudgetPerIpPerMinute = 1;
        using var scope = harness.CreateScope();
        var context = TileCacheTestHarness.CreateHttpContext();
        var controller = CreateController(scope, context);

        Assert.IsType<FileContentResult>(await controller.GetTile(5, 27, 1));
        var rejected = Assert.IsType<ObjectResult>(await controller.GetTile(5, 28, 1));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(
            TilesController.BudgetRetryAfterSeconds.ToString(),
            context.Response.Headers.RetryAfter);
        var diagnostic = AssertDiagnostic(harness.Logs, TileCacheDiagnosticEventIds.ClientBudgetRejected);
        Assert.Equal("outbound-client", diagnostic.Fields["BudgetScope"]);
    }

    /// <summary>Proves a literal-IP cross-host redirect is rejected without recording supplied data.</summary>
    [Fact]
    public async Task CrossHostRedirect_DoesNotRecordLiteralIpOrSecretUri()
    {
        const string suppliedIp = "192.0.2.123";
        const string suppliedSecret = "redirect-secret";
        var upstream = new RecordingTileHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri($"https://{suppliedIp}/tiles.png?token={suppliedSecret}");
            return Task.FromResult(response);
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var scope = harness.CreateScope();
        SetHttpContext(scope, TileCacheTestHarness.CreateHttpContext());
        var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();

        var result = await service.RetrieveTileAsync("5", "18", "27", CanonicalTileUrl(5, 18, 27));

        Assert.Equal(TileRetrievalStatus.TransientFailure, result.Status);
        Assert.Contains(harness.Logs.Entries,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Message.Contains("different host", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(harness.Logs.Entries, entry =>
            Contains(entry, suppliedIp) || Contains(entry, suppliedSecret));
    }

    /// <summary>Proves harness disposal cancels and awaits background refresh before deleting cache data.</summary>
    [Fact]
    public async Task HarnessDisposal_AwaitsTrackedStaleRefresh()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingTileHandler(async (request, token) =>
        {
            if (request.Headers.IfNoneMatch.Count == 0)
            {
                return ExpiredPngResponse();
            }

            refreshStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                refreshCancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The refresh must be cancelled during cleanup.");
        });
        var harness = new TileCacheTestHarness(upstream);
        using (var scope = harness.CreateScope())
        {
            SetHttpContext(scope, TileCacheTestHarness.CreateHttpContext());
            var service = scope.ServiceProvider.GetRequiredService<TileCacheService>();
            await SeedExpiredTileAsync(service, harness.CacheDirectory, 5, 18, 28);
            await service.RetrieveTileAsync("5", "18", "28", CanonicalTileUrl(5, 18, 28));
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await harness.DisposeAsync();

        await refreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync(
            "5_18_28", TimeSpan.FromMilliseconds(100)));
        Assert.False(Directory.Exists(harness.CacheDirectory));
    }

    /// <summary>Seeds one expired low-zoom tile through the normal cache-write path.</summary>
    private static async Task SeedExpiredTileAsync(
        TileCacheService service,
        string cacheDirectory,
        int zoom,
        int x,
        int y)
    {
        Assert.True(await service.CacheTileAsync(CanonicalTileUrl(zoom, x, y),
            zoom.ToString(), x.ToString(), y.ToString()));
        var tilePath = service.GetTileFilePathForTesting(
            zoom.ToString(), x.ToString(), y.ToString());
        await File.WriteAllTextAsync(
            tilePath + ".meta",
            JsonSerializer.Serialize(new TileSidecarMetadata
            {
                ETag = "\"phase1-stale\"",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
            }));
        TileCacheService.ResetStaticStateForTesting();
    }

    /// <summary>Creates a controller and shares its request context with the scoped cache service.</summary>
    private static TilesController CreateController(IServiceScope scope, DefaultHttpContext context)
    {
        SetHttpContext(scope, context);
        return new TilesController(
            scope.ServiceProvider.GetRequiredService<ILogger<TilesController>>(),
            scope.ServiceProvider.GetRequiredService<TileCacheService>(),
            scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    /// <summary>Assigns one request context to the scoped tile service.</summary>
    private static void SetHttpContext(IServiceScope scope, DefaultHttpContext context) =>
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

    /// <summary>Returns the only diagnostic with the requested stable identifier.</summary>
    private static TestLogProvider.TestLogEntry AssertDiagnostic(
        TestLogProvider logs,
        TileCacheDiagnosticEventIds eventId) =>
        Assert.Single(logs.Entries, entry => entry.EventId.Id == (int)eventId);

    /// <summary>Asserts exactly one cancellation event with the expected bounded stage.</summary>
    private static void AssertCancellation(TestLogProvider logs, string expectedStage)
    {
        var diagnostic = AssertDiagnostic(logs, TileCacheDiagnosticEventIds.Cancellation);
        Assert.Equal(expectedStage, diagnostic.Fields["CancellationStage"]);
    }

    /// <summary>Asserts cancellation was not emitted.</summary>
    private static void AssertNoCancellation(TestLogProvider logs) =>
        Assert.DoesNotContain(logs.Entries,
            entry => entry.EventId.Id == (int)TileCacheDiagnosticEventIds.Cancellation);

    /// <summary>Asserts caller cancellation was not also classified as an upstream failure.</summary>
    private static void AssertNoUpstreamFailure(TestLogProvider logs) =>
        Assert.DoesNotContain(logs.Entries,
            entry => entry.EventId.Id == (int)TileCacheDiagnosticEventIds.UpstreamFailure);

    /// <summary>Builds one canonical fake-intercepted tile URL.</summary>
    private static string CanonicalTileUrl(int zoom, int x, int y) =>
        $"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png";

    /// <summary>Builds an immediately expired cacheable tile response.</summary>
    private static HttpResponseMessage ExpiredPngResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        };
        response.Headers.ETag = EntityTagHeaderValue.Parse("\"phase1-stale\"");
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
        return response;
    }

    /// <summary>Checks every captured log surface for forbidden supplied redirect data.</summary>
    private static bool Contains(TestLogProvider.TestLogEntry entry, string value) =>
        entry.Message.Contains(value, StringComparison.Ordinal) ||
        entry.Fields.Values.Any(field =>
            field?.ToString()?.Contains(value, StringComparison.Ordinal) == true) ||
        entry.Exception?.ToString().Contains(value, StringComparison.Ordinal) == true;
}
