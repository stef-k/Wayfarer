using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Reproduces production findings from the issue #385 Phase 3 reviews.</summary>
[Collection("OutboundBudget")]
public sealed class TileCachePhase3ProductionReviewTests
{
    /// <summary>Cold and stale shared scopes forward only the deployment origin as Referer.</summary>
    [Fact]
    public async Task SharedColdAndStaleRequests_SendOriginOnlyReferer()
    {
        var upstream = new RecordingTileHandler((request, _) =>
        {
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            return Task.FromResult(PngResponse([1, 2, 3], TimeSpan.FromHours(1), "\"origin\""));
        });
        await using var harness = new TileCacheTestHarness(
            upstream,
            "other.example.com;WAYFARER.EXAMPLE.COM.");
        const string privateReferer =
            "https://wayfarer.example.com.:8443/trips/private-user?token=secret";
        SeedExpiredLowZoomTile(harness.CacheDirectory, 5, 1, 2, [4, 5, 6]);

        Assert.Equal(
            StatusCodes.Status200OK,
            (await RequestTileAsync(
                harness,
                5,
                1,
                1,
                referer: privateReferer,
                requestHost: "wayfarer.example.com.:8443")).StatusCode);
        Assert.Equal(
            StatusCodes.Status200OK,
            (await RequestTileAsync(
                harness,
                5,
                1,
                2,
                referer: privateReferer,
                requestHost: "wayfarer.example.com.:8443")).StatusCode);
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync(
            "5_1_2", TimeSpan.FromSeconds(2)));

        var requests = upstream.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request =>
        {
            var referer = Assert.Single(request.Headers["Referer"]);
            Assert.Equal("https://wayfarer.example.com:8443/", referer);
            Assert.DoesNotContain("trips", referer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-user", referer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", referer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", referer, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Wildcard, unmatched, and private request hosts never identify the deployment upstream.</summary>
    [Theory]
    [InlineData("*", "attacker.example.com")]
    [InlineData("*.example.com", "attacker.example.com")]
    [InlineData("wayfarer.example.com", "10.0.0.8")]
    [InlineData("app.localhost", "app.localhost")]
    [InlineData("child.app.localhost", "child.app.localhost")]
    [InlineData("site.local", "site.local")]
    [InlineData("service.internal", "service.internal")]
    [InlineData("child.service.internal", "child.service.internal")]
    [InlineData("host.home.arpa", "host.home.arpa")]
    [InlineData("site.test", "site.test")]
    [InlineData("site.invalid", "site.invalid")]
    [InlineData("site.example", "site.example")]
    [InlineData("hidden.onion", "hidden.onion")]
    [InlineData("private.alt", "private.alt")]
    public async Task UntrustedRequestHost_IsNotForwardedOrLogged(
        string allowedHosts,
        string requestHost)
    {
        var upstream = new RecordingTileHandler();
        await using var harness = new TileCacheTestHarness(upstream, allowedHosts);

        Assert.Equal(
            StatusCodes.Status200OK,
            (await RequestTileAsync(
                harness,
                5,
                8,
                8,
                referer: $"https://{requestHost}/map",
                requestHost: requestHost)).StatusCode);

        var request = Assert.Single(upstream.Requests);
        Assert.DoesNotContain("Referer", request.Headers.Keys);
        Assert.DoesNotContain(
            harness.Logs.Entries,
            entry => entry.Message.Contains(requestHost, StringComparison.OrdinalIgnoreCase) ||
                     entry.Fields.Values.Any(value =>
                         value?.ToString()?.Contains(
                          requestHost,
                          StringComparison.OrdinalIgnoreCase) == true));
    }

    /// <summary>Unsafe-only host configuration leaves the production warning enabled.</summary>
    [Theory]
    [InlineData("*")]
    [InlineData("*.example.com")]
    [InlineData("app.localhost")]
    [InlineData("service.internal;host.home.arpa")]
    [InlineData("site.test;site.invalid;site.example;hidden.onion;private.alt")]
    public void UnsafeAllowedHosts_AreNotTrustworthy(string allowedHosts)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = allowedHosts
            })
            .Build();

        Assert.False(TileCacheService.HasTrustworthyAllowedHosts(configuration));
    }

    /// <summary>At least one exact public hostname clears the missing-Referer configuration warning.</summary>
    [Fact]
    public void ExactPublicAllowedHosts_AreTrustworthy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "*.example.com;WAYFARER.EXAMPLE.COM."
            })
            .Build();

        Assert.True(TileCacheService.HasTrustworthyAllowedHosts(configuration));
    }

    /// <summary>The installer preserves a valid existing hostname before considering Certbot fallback.</summary>
    [Fact]
    public void InstallerHostnameSelection_ValidatesCandidatesInSafePrecedenceOrder()
    {
        var installerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "deployment",
            "install.sh"));
        var installer = File.ReadAllText(installerPath);
        var explicitSelection = installer.IndexOf(
            "is_valid_public_host_configuration \"$ALLOWED_HOSTS\"",
            StringComparison.Ordinal);
        var existingSelection = installer.IndexOf(
            "is_valid_public_host_configuration \"${EXISTING_ALLOWED_HOSTS:-}\"",
            StringComparison.Ordinal);
        var certbotSelection = installer.IndexOf(
            "is_valid_public_host_configuration \"$CERTBOT_DOMAIN\"",
            StringComparison.Ordinal);

        Assert.True(explicitSelection >= 0);
        Assert.True(existingSelection > explicitSelection);
        Assert.True(certbotSelection > existingSelection);
    }

    /// <summary>Cleanup retires null-provider legacy state without deleting adopted OSM storage.</summary>
    [Fact]
    public async Task LegacyCleanup_PreservesAdoptedOsmFileAndMetadata()
    {
        await using var harness = new TileCacheTestHarness();
        var adoptedPath = Path.Combine(harness.CacheDirectory, "9_2_2.png");
        var legacyPath = Path.Combine(harness.CacheDirectory, "9_3_3.png");
        await File.WriteAllBytesAsync(adoptedPath, [2]);
        await File.WriteAllBytesAsync(legacyPath, [3]);
        using (var seedScope = harness.CreateScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.TileCacheMetadata.AddRange(
                LegacyMetadata(9, 2, 2, adoptedPath),
                LegacyMetadata(9, 3, 3, legacyPath));
            await database.SaveChangesAsync();
        }

        Assert.Equal(
            StatusCodes.Status200OK,
            (await RequestTileAsync(harness, 9, 2, 2)).StatusCode);
        harness.Settings.TileProviderKey = "custom";
        harness.Settings.TileProviderUrlTemplate =
            "https://tiles.example.test/{z}/{x}/{y}.png";

        using (var cleanupScope = harness.CreateScope())
        {
            Assert.Equal(
                1,
                await cleanupScope.ServiceProvider.GetRequiredService<TileCacheService>()
                    .RetireLegacyCacheBatchAsync(CancellationToken.None));
        }

        using var verifyScope = harness.CreateScope();
        var remaining = verifyScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .TileCacheMetadata
            .ToArray();
        var adopted = Assert.Single(remaining);
        Assert.NotNull(adopted.ProviderIdentity);
        Assert.Equal(adoptedPath, adopted.TileFilePath);
        Assert.True(File.Exists(adoptedPath));
        Assert.False(File.Exists(legacyPath));
    }

    /// <summary>A cancelling last waiter cannot overlap a replacement for the same owned key.</summary>
    [Fact]
    public async Task LastWaiterCancellation_WaitsForRunnerThenUsesPersistedCache()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var contacts = 0;
        string? persistedPath = null;
        var upstream = new RecordingTileHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref contacts) == 1)
            {
                firstStarted.TrySetResult();
            }
            else
            {
                secondStarted.TrySetResult();
            }

            // Deliberately ignore transport cancellation to expose replacement overlap.
            await releaseTransport.Task;
            Directory.CreateDirectory(Path.GetDirectoryName(persistedPath!)!);
            await File.WriteAllBytesAsync(persistedPath!, [7, 8, 9]);
            return PngResponse([7, 8, 9], TimeSpan.FromHours(1));
        });
        await using var harness = new TileCacheTestHarness(upstream);
        var provider = TileProviderCatalog.CreateCacheIdentity(
            harness.Settings.TileProviderKey,
            harness.Settings.TileProviderUrlTemplate);
        persistedPath = Path.Combine(
            harness.CacheDirectory, provider.Fingerprint, "5_4_4.png");
        using var cancellation = new CancellationTokenSource();
        var first = RequestTileAsync(harness, 5, 4, 4, cancellationToken: cancellation.Token);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var replacement = RequestTileAsync(harness, 5, 4, 4);
        try
        {
            Assert.NotSame(
                secondStarted.Task,
                await Task.WhenAny(secondStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(250))));
        }
        finally
        {
            releaseTransport.TrySetResult();
        }

        Assert.Equal(StatusCodes.Status200OK, (await replacement).StatusCode);
        Assert.Equal(1, contacts);
    }

    /// <summary>Followers consume and exactly release the existing per-client waiting capacity.</summary>
    [Fact]
    public async Task CoalescedFollowers_RespectPerClientWaitingCapAndReleaseCapacity()
    {
        TileWorkScheduler.ResetForTesting();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leader = TileWorkScheduler.ExecuteForegroundAsync(
            "provider:5:5:5",
            "same-client",
            async _ =>
            {
                await release.Task;
                return TileRetrievalResult.Success([5]);
            },
            CancellationToken.None);
        var cancellations = Enumerable.Range(0, TileWorkScheduler.PerClientQueueCapacity)
            .Select(_ => new CancellationTokenSource())
            .ToArray();
        var followers = cancellations
            .Select(cancellation => TileWorkScheduler.ExecuteForegroundAsync(
                "provider:5:5:5",
                "same-client",
                _ => throw new InvalidOperationException("Follower cannot own transport."),
                cancellation.Token))
            .ToArray();

        var excess = await TileWorkScheduler.ExecuteForegroundAsync(
                "provider:5:5:5",
                "same-client",
                _ => throw new InvalidOperationException("Excess follower cannot own transport."),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TileRetrievalStatus.BudgetRejected, excess.Status);

        foreach (var cancellation in cancellations.Take(12))
        {
            cancellation.Cancel();
        }

        foreach (var follower in followers.Take(12))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);
        }

        var replacements = Enumerable.Range(0, 12)
            .Select(_ => TileWorkScheduler.ExecuteForegroundAsync(
                "provider:5:5:5",
                "same-client",
                _ => throw new InvalidOperationException("Follower cannot own transport."),
                CancellationToken.None))
            .ToArray();
        var secondExcess = await TileWorkScheduler.ExecuteForegroundAsync(
                "provider:5:5:5",
                "same-client",
                _ => throw new InvalidOperationException("Excess follower cannot own transport."),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TileRetrievalStatus.BudgetRejected, secondExcess.Status);

        release.TrySetResult();
        Assert.Equal(TileRetrievalStatus.Success, (await leader).Status);
        Assert.All(
            await Task.WhenAll(followers.Skip(12).Concat(replacements)),
            result => Assert.Equal(TileRetrievalStatus.Success, result.Status));
    }

    /// <summary>Authenticated users behind one IP receive independent scheduler ownership.</summary>
    [Fact]
    public async Task AuthenticatedUsersSharingIp_HaveIndependentSchedulerQuotas()
    {
        var sixStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondUserStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var upstream = new RecordingTileHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/7/1.png", StringComparison.Ordinal) == true)
            {
                secondUserStarted.TrySetResult();
            }

            if (Interlocked.Increment(ref started) == TileWorkScheduler.PerClientConcurrency)
            {
                sixStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return PngResponse([1], TimeSpan.FromHours(1));
        });
        await using var harness = new TileCacheTestHarness(upstream);
        var userA = AuthenticatedUser("phase3-user-a");
        var userB = AuthenticatedUser("phase3-user-b");
        var firstUserRequests = Enumerable.Range(1, TileWorkScheduler.PerClientConcurrency)
            .Select(x => RequestTileAsync(harness, 5, x, 1, userA))
            .ToArray();
        await sixStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondUserRequest = RequestTileAsync(harness, 5, 7, 1, userB);
        try
        {
            await secondUserStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.All(
            await Task.WhenAll(firstUserRequests.Append(secondUserRequest)),
            result => Assert.Equal(StatusCodes.Status200OK, result.StatusCode));
    }

    /// <summary>Shutdown cancels and boundedly drains foreground and stale-refresh ownership.</summary>
    [Fact]
    public async Task ApplicationStopping_CancelsAndBoundedlyDrainsTileWork()
    {
        var foregroundStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var foregroundCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RecordingTileHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/8/8.png", StringComparison.Ordinal) == true &&
                request.Headers.IfNoneMatch.Count == 0)
            {
                return PngResponse([8], TimeSpan.Zero, "\"shutdown\"");
            }

            var cancellationSignal = request.Headers.IfNoneMatch.Count > 0
                ? backgroundCancelled
                : foregroundCancelled;
            (request.Headers.IfNoneMatch.Count > 0 ? backgroundStarted : foregroundStarted)
                .TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationSignal.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Shutdown-cancelled transport continued.");
        });
        await using var harness = new TileCacheTestHarness(upstream);
        SeedExpiredLowZoomTile(harness.CacheDirectory, 5, 8, 8, [8]);
        Assert.Equal(StatusCodes.Status200OK, (await RequestTileAsync(harness, 5, 8, 8)).StatusCode);
        await backgroundStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var foreground = RequestTileAsync(harness, 5, 9, 9);
        await foregroundStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            TileCacheService.StopOutboundBudget();
            await Task.WhenAll(foregroundCancelled.Task, backgroundCancelled.Task)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync(
                "5_8_8", TimeSpan.FromMilliseconds(100)));
            await foreground;
            var rejected = await TileWorkScheduler.ExecuteForegroundAsync(
                "provider:shutdown:new",
                "client",
                _ => Task.FromResult(TileRetrievalResult.Success([1])),
                CancellationToken.None);
            Assert.Equal(TileRetrievalStatus.BudgetRejected, rejected.Status);
        }
        finally
        {
            TileWorkScheduler.ResetForTesting();
            await TileCacheService.CancelAndWaitForRefreshesForTestingAsync(
                TimeSpan.FromSeconds(1));
        }
    }

    private static TileCacheMetadata LegacyMetadata(
        int zoom,
        int x,
        int y,
        string path) => new()
    {
        Zoom = zoom,
        X = x,
        Y = y,
        TileLocation = new Point(x, y),
        LastAccessed = DateTime.UtcNow,
        Size = 1,
        TileFilePath = path,
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private static ClaimsPrincipal AuthenticatedUser(string userId) => new(
        new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)],
            "Phase3Review"));

    private static void SeedExpiredLowZoomTile(
        string cacheDirectory,
        int zoom,
        int x,
        int y,
        byte[] bytes)
    {
        var path = Path.Combine(cacheDirectory, $"{zoom}_{x}_{y}.png");
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(
            path + ".meta",
            JsonSerializer.Serialize(new TileSidecarMetadata
            {
                ETag = "\"phase3-review\"",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            }));
    }

    private static async Task<LocalTileOutcome> RequestTileAsync(
        TileCacheTestHarness harness,
        int zoom,
        int x,
        int y,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default,
        string? referer = null,
        string? requestHost = null)
    {
        using var scope = harness.CreateScope();
        var context = TileCacheTestHarness.CreateHttpContext(cancellationToken);
        context.User = user ?? new ClaimsPrincipal(new ClaimsIdentity());
        context.Request.Path = $"/trips/private-user/{zoom}/{x}/{y}";
        context.Request.QueryString = new QueryString("?token=secret");
        if (requestHost != null)
        {
            context.Request.Host = new HostString(requestHost);
        }

        if (referer != null)
        {
            context.Request.Headers.Referer = referer;
        }

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
            FileContentResult file =>
                new LocalTileOutcome(StatusCodes.Status200OK, file.FileContents),
            ObjectResult value =>
                new LocalTileOutcome(value.StatusCode ?? StatusCodes.Status200OK, null),
            StatusCodeResult status => new LocalTileOutcome(status.StatusCode, null),
            _ => throw new InvalidOperationException(
                $"Unexpected tile result {result.GetType().Name}.")
        };
    }

    private static HttpResponseMessage PngResponse(
        byte[] bytes,
        TimeSpan maxAge,
        string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = maxAge };
        if (etag != null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private sealed record LocalTileOutcome(int StatusCode, byte[]? Bytes);
}
