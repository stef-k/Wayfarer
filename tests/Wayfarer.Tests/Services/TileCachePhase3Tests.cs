using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Proves the bounded scheduling and provider-isolation behavior introduced by issue #385 Phase 3.
/// </summary>
[Collection("OutboundBudget")]
public sealed class TileCachePhase3Tests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates a fixture that records deterministic performance evidence.</summary>
    public TileCachePhase3Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>One follower may leave without cancelling shared transport owned by another waiter.</summary>
    [Fact]
    public async Task FollowerCancellation_PreservesSharedWork_AndLastWaiterRemovesFlight()
    {
        TileWorkScheduler.ResetForTesting();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = 0;
        var leader = TileWorkScheduler.ExecuteForegroundAsync(
            "provider:5:1:1",
            "client-a",
            async token =>
            {
                Interlocked.Increment(ref operations);
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return TileRetrievalResult.Success([1]);
            },
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var followerCancellation = new CancellationTokenSource();
        var follower = TileWorkScheduler.ExecuteForegroundAsync(
            "provider:5:1:1",
            "client-b",
            _ => throw new InvalidOperationException("Follower must not create another operation."),
            followerCancellation.Token);
        followerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        release.TrySetResult();
        Assert.Equal(TileRetrievalStatus.Success, (await leader).Status);
        Assert.Equal(1, operations);

        using var lastWaiterCancellation = new CancellationTokenSource();
        var abandonedStarted =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportCancelled =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = TileWorkScheduler.ExecuteForegroundAsync(
            "provider:5:2:2",
            "client-a",
            async token =>
            {
                try
                {
                    abandonedStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return TileRetrievalResult.Success([2]);
                }
                finally
                {
                    transportCancelled.TrySetResult();
                }
            },
            lastWaiterCancellation.Token);
        await abandonedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lastWaiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        await transportCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replacement = await TileWorkScheduler.ExecuteForegroundAsync(
            "provider:5:2:2",
            "client-a",
            _ => Task.FromResult(TileRetrievalResult.Success([3])),
            CancellationToken.None);
        Assert.Equal(new byte[] { 3 }, replacement.TileData);
    }

    /// <summary>Per-client queued work is explicitly rejected before shared state can grow without bound.</summary>
    [Fact]
    public async Task PerClientQueueCap_RejectsThirtyFirstUniqueSeries()
    {
        TileWorkScheduler.ResetForTesting();
        using var cancellation = new CancellationTokenSource();
        var accepted = Enumerable.Range(
                0,
                TileWorkScheduler.PerClientConcurrency + TileWorkScheduler.PerClientQueueCapacity)
            .Select(index => TileWorkScheduler.ExecuteForegroundAsync(
                $"provider:5:{index}:1",
                "one-client",
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return TileRetrievalResult.Success([1]);
                },
                cancellation.Token))
            .ToArray();

        var rejected = await TileWorkScheduler.ExecuteForegroundAsync(
            "provider:5:overflow:1",
            "one-client",
            _ => Task.FromResult(TileRetrievalResult.Success([9])),
            CancellationToken.None);

        Assert.Equal(TileRetrievalStatus.BudgetRejected, rejected.Status);
        Assert.Equal(TilesController.BudgetRetryAfterSeconds, rejected.RetryAfterSeconds);

        cancellation.Cancel();
        foreach (var task in accepted)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
    }

    /// <summary>The shared foreground queue rejects the first unique series beyond its global bound.</summary>
    [Fact]
    public async Task GlobalQueueCap_RejectsExplicitly()
    {
        TileWorkScheduler.ResetForTesting();
        using var cancellation = new CancellationTokenSource();
        var acceptedCount =
            TileWorkScheduler.ForegroundConcurrency + TileWorkScheduler.ForegroundQueueCapacity;
        var accepted = Enumerable.Range(0, acceptedCount)
            .Select(index => TileWorkScheduler.ExecuteForegroundAsync(
                $"provider:6:{index}:1",
                $"client-{index}",
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return TileRetrievalResult.Success([1]);
                },
                cancellation.Token))
            .ToArray();

        var rejected = await TileWorkScheduler.ExecuteForegroundAsync(
            "provider:6:overflow:1",
            "overflow-client",
            _ => Task.FromResult(TileRetrievalResult.Success([9])),
            CancellationToken.None);

        Assert.Equal(TileRetrievalStatus.BudgetRejected, rejected.Status);
        cancellation.Cancel();
        foreach (var task in accepted)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
    }

    /// <summary>Queued foreground capacity receives a released token before background maintenance.</summary>
    [Fact]
    public async Task ForegroundBudgetWaiter_PrecedesBackgroundRefresh()
    {
        TileCacheService.OutboundBudget.DrainForTesting();
        try
        {
            var foreground = TileCacheService.OutboundBudget.AcquireDetailedAsync(
                CancellationToken.None, TileWorkPriority.Foreground);
            await Task.Delay(25);
            var background = await TileCacheService.OutboundBudget.AcquireDetailedAsync(
                CancellationToken.None, TileWorkPriority.Background);
            TileCacheService.OutboundBudget.ReleaseOneForTesting();

            Assert.False(background.Acquired);
            Assert.True((await foreground.WaitAsync(TimeSpan.FromSeconds(2))).Acquired);

            using var backgroundContact = TileCacheService.OutboundBudget.TryAcquireBackgroundContact();
            Assert.NotNull(backgroundContact);
            Assert.Null(TileCacheService.OutboundBudget.TryAcquireBackgroundContact());
        }
        finally
        {
            TileCacheService.OutboundBudget.ResetForTesting();
        }
    }

    /// <summary>Concurrent requests for one provider tile must share one upstream fetch series.</summary>
    [Fact]
    public async Task SameProviderConcurrentColdMisses_ShareOneUpstreamSeries()
    {
        var firstContact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingTileHandler(async (_, cancellationToken) =>
        {
            firstContact.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return PngResponse([1, 2, 3, 4]);
        });
        await using var harness = new TileCacheTestHarness(upstream);

        var first = RequestTileAsync(harness, 5, 7, 8);
        await firstContact.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = RequestTileAsync(harness, 5, 7, 8);
        await Task.Delay(50);
        release.TrySetResult();

        var outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, outcome => Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode));
        Assert.Single(upstream.Requests);
    }

    /// <summary>A provider change must not expose bytes cached for the preceding provider.</summary>
    [Fact]
    public async Task DifferentProviders_DoNotShareCoordinateCache()
    {
        var sequence = 0;
        var upstream = new RecordingTileHandler((_, _) =>
            Task.FromResult(PngResponse([(byte)Interlocked.Increment(ref sequence)])));
        await using var harness = new TileCacheTestHarness(upstream);

        var first = await RequestTileAsync(harness, 5, 7, 8);
        harness.Settings.TileProviderKey = "custom";
        harness.Settings.TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png";
        var second = await RequestTileAsync(harness, 5, 7, 8);

        Assert.Equal(StatusCodes.Status200OK, first.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, second.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.NotEqual(first.Bytes, second.Bytes);
    }

    /// <summary>Only canonical OSM may lazily adopt unscoped bytes without provider traffic.</summary>
    [Fact]
    public async Task LegacyOsm_IsAdoptedWithoutDownload_ButCustomProviderCannotUseIt()
    {
        await using var harness = new TileCacheTestHarness();
        var legacyPath = Path.Combine(harness.CacheDirectory, "9_7_8.png");
        await File.WriteAllBytesAsync(legacyPath, [7, 8, 9]);
        using (var seedScope = harness.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.TileCacheMetadata.Add(new TileCacheMetadata
            {
                Zoom = 9,
                X = 7,
                Y = 8,
                TileLocation = new NetTopologySuite.Geometries.Point(7, 8),
                LastAccessed = DateTime.UtcNow,
                Size = 3,
                TileFilePath = legacyPath,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            });
            await db.SaveChangesAsync();
        }

        var osm = await RequestTileAsync(harness, 9, 7, 8);
        Assert.Equal(new byte[] { 7, 8, 9 }, osm.Bytes);
        Assert.Empty(harness.Upstream.Requests);
        using (var verifyScope = harness.CreateScope())
        {
            var adopted = Assert.Single(
                verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TileCacheMetadata);
            Assert.NotNull(adopted.ProviderIdentity);
        }

        harness.Settings.TileProviderKey = "custom";
        harness.Settings.TileProviderUrlTemplate = "https://tiles.example.test/{z}/{x}/{y}.png";
        var custom = await RequestTileAsync(harness, 9, 7, 8);

        Assert.Equal(StatusCodes.Status200OK, custom.StatusCode);
        Assert.Single(harness.Upstream.Requests);
        Assert.NotEqual(osm.Bytes, custom.Bytes);
    }

    /// <summary>A 24-tile cold viewport completes progressively under the unchanged 12/2s budget.</summary>
    [Fact]
    public async Task ControlledColdViewport_CompletesProgressivelyWithinDerivedCeiling()
    {
        const int tileCount = 24;
        var latency = TimeSpan.FromMilliseconds(100);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var upstream = new RecordingTileHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(latency, cancellationToken);
                return PngResponse([1, 2, 3, 4]);
            },
            () => stopwatch.Elapsed);
        await using var harness = new TileCacheTestHarness(upstream);

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, tileCount)
                .Select(x => RequestTileAsync(harness, 5, x, 1)));
        stopwatch.Stop();
        var starts = upstream.Requests.Select(request => request.StartTime).Order().ToArray();
        var derivedCeiling = TimeSpan.FromSeconds(6.6);

        Assert.All(outcomes, outcome => Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode));
        Assert.Equal(tileCount, starts.Length);
        Assert.Equal(TileCacheService.OutboundBudget.BurstCapacity, 12);
        Assert.Contains(starts, start => start >= TimeSpan.FromMilliseconds(500));
        Assert.True(stopwatch.Elapsed <= derivedCeiling + TimeSpan.FromSeconds(2));
        _output.WriteLine(
            "N={0}, B={1}, R=2/s, L={2}ms, queue={3}, ceiling={4:F1}s, actual={5:F3}s",
            tileCount,
            TileCacheService.OutboundBudget.BurstCapacity,
            latency.TotalMilliseconds,
            TileWorkScheduler.ForegroundQueueCapacity,
            derivedCeiling.TotalSeconds,
            stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>Executes one controller request in an independent service scope.</summary>
    private static async Task<LocalTileOutcome> RequestTileAsync(
        TileCacheTestHarness harness,
        int zoom,
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        using var scope = harness.CreateScope();
        var context = TileCacheTestHarness.CreateHttpContext(cancellationToken);
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var controller = new TilesController(
            scope.ServiceProvider.GetRequiredService<ILogger<TilesController>>(),
            scope.ServiceProvider.GetRequiredService<TileCacheService>(),
            scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.GetTile(zoom, x, y);
        return result switch
        {
            FileContentResult file => new LocalTileOutcome(StatusCodes.Status200OK, file.FileContents),
            ObjectResult value => new LocalTileOutcome(value.StatusCode ?? StatusCodes.Status200OK, null),
            StatusCodeResult status => new LocalTileOutcome(status.StatusCode, null),
            _ => throw new InvalidOperationException($"Unexpected tile result {result.GetType().Name}.")
        };
    }

    /// <summary>Builds one deterministic cacheable PNG response.</summary>
    private static HttpResponseMessage PngResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Headers.CacheControl =
            new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        return response;
    }

    /// <summary>Captures local status and successful response bytes.</summary>
    private sealed record LocalTileOutcome(int StatusCode, byte[]? Bytes);
}
