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

namespace Wayfarer.Tests.Services;

/// <summary>
/// Proves the bounded scheduling and provider-isolation behavior introduced by issue #385 Phase 3.
/// </summary>
[Collection("OutboundBudget")]
public sealed class TileCachePhase3Tests
{
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
        // This legacy priority test exercises the token semaphore, not provider-contact rejection.
        TileCacheService.OutboundBudget.SetAcquireOverrideForTesting(null);
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

    /// <summary>A cold viewport admits another provider contact whenever one client slot is released.</summary>
    [Fact]
    public async Task ControlledColdViewport_CompletesProgressivelyWithinDerivedCeiling()
    {
        const int tileCount = 24;
        var transport = new GatedRecordingTransport();
        await using var harness = new TileCacheTestHarness(transport.Handler);
        var requests = Enumerable.Range(0, tileCount)
            .Select(x => RequestTileAsync(harness, 5, x, 1))
            .ToArray();

        try
        {
            await transport.SixEntered.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TileWorkScheduler.PerClientConcurrency, transport.EnteredCount);
            Assert.Equal(TileWorkScheduler.PerClientConcurrency, transport.ActiveCount);
            Assert.Equal(TileWorkScheduler.PerClientConcurrency, transport.MaxActiveCount);
            Assert.False(transport.SeventhEntered.IsCompleted);
            Assert.True(TileWorkScheduler.HasQueuedForeground);

            transport.ReleaseOne();
            await transport.SeventhEntered.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TileWorkScheduler.PerClientConcurrency + 1, transport.EnteredCount);
            Assert.Equal(TileWorkScheduler.PerClientConcurrency, transport.MaxActiveCount);

            transport.ReleaseAll();
            var outcomes = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(tileCount, transport.EnteredCount);
            Assert.All(outcomes, outcome => Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode));
        }
        finally
        {
            transport.ReleaseAll();
            try
            {
                await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                transport.Dispose();
            }
        }
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

    /// <summary>Records provider-contact admission and holds each contact behind a test-owned gate.</summary>
    private sealed class GatedRecordingTransport : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<TaskCompletionSource> _completionGates = new();
        private readonly TaskCompletionSource _sixEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _seventhEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _releaseAll;
        private int _enteredCount;
        private int _activeCount;
        private int _maxActiveCount;

        /// <summary>Initializes the recording handler used by the real tile-cache transport path.</summary>
        public GatedRecordingTransport()
        {
            Handler = new RecordingTileHandler(HandleAsync);
        }

        /// <summary>Gets the handler injected into the tile-cache test harness.</summary>
        public RecordingTileHandler Handler { get; }

        /// <summary>Completes when the client's full six-contact allowance has entered.</summary>
        public Task SixEntered => _sixEntered.Task;

        /// <summary>Completes when progressive admission permits the seventh contact to enter.</summary>
        public Task SeventhEntered => _seventhEntered.Task;

        /// <summary>Gets the number of contacts that have entered the transport.</summary>
        public int EnteredCount { get { lock (_sync) return _enteredCount; } }

        /// <summary>Gets the number of contacts currently held by the transport.</summary>
        public int ActiveCount { get { lock (_sync) return _activeCount; } }

        /// <summary>Gets the greatest number of simultaneously active contacts.</summary>
        public int MaxActiveCount { get { lock (_sync) return _maxActiveCount; } }

        /// <summary>Releases exactly one currently active contact.</summary>
        public void ReleaseOne()
        {
            TaskCompletionSource gate;
            lock (_sync)
            {
                gate = _completionGates.Dequeue();
            }

            gate.TrySetResult();
        }

        /// <summary>Releases every active and future contact so cleanup cannot strand work.</summary>
        public void ReleaseAll()
        {
            TaskCompletionSource[] gates;
            lock (_sync)
            {
                _releaseAll = true;
                gates = _completionGates.ToArray();
                _completionGates.Clear();
            }

            foreach (var gate in gates)
            {
                gate.TrySetResult();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            ReleaseAll();
            Handler.Dispose();
        }

        /// <summary>Records entry, awaits its completion gate, and returns cacheable tile bytes.</summary>
        private async Task<HttpResponseMessage> HandleAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _enteredCount++;
                _activeCount++;
                _maxActiveCount = Math.Max(_maxActiveCount, _activeCount);
                if (!_releaseAll)
                {
                    _completionGates.Enqueue(gate);
                }
                else
                {
                    gate.TrySetResult();
                }

                if (_enteredCount == TileWorkScheduler.PerClientConcurrency)
                {
                    _sixEntered.TrySetResult();
                }

                if (_enteredCount == TileWorkScheduler.PerClientConcurrency + 1)
                {
                    _seventhEntered.TrySetResult();
                }
            }

            try
            {
                await gate.Task.WaitAsync(cancellationToken);
                return PngResponse([1, 2, 3, 4]);
            }
            finally
            {
                lock (_sync)
                {
                    _activeCount--;
                }
            }
        }
    }

    /// <summary>Captures local status and successful response bytes.</summary>
    private sealed record LocalTileOutcome(int StatusCode, byte[]? Bytes);
}
