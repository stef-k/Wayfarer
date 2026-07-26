using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Holds the focused production-review regressions for the five Phase 2A corrections.
/// </summary>
public sealed partial class TileCacheRetryStatusTests
{
    /// <summary>Proves redirects share the operation-wide three-contact and global-budget ceiling.</summary>
    [Fact]
    public async Task RedirectChain_StopsAfterThreeTotalProviderContacts()
    {
        var acquisitions = 0;
        var upstream = new RecordingTileHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath;
            var response = path switch
            {
                "/redirect-2" => RedirectTo("/redirect-3"),
                "/redirect-3" => RedirectTo("/redirect-4"),
                "/redirect-4" => PngResponse(),
                _ => RedirectTo("/redirect-2")
            };
            return Task.FromResult(response);
        });
        using var harness = new TileCacheTestHarness(upstream);
        TileCacheService.OutboundBudget.SetAcquireOverrideForTesting(_ =>
        {
            Interlocked.Increment(ref acquisitions);
            return Task.FromResult(new OutboundBudgetAcquisition(true, TimeSpan.Zero));
        });
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = Assert.IsType<ObjectResult>(await controller.GetTile(5, 22, 1));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(3, harness.Upstream.Requests.Count);
        Assert.Equal(3, acquisitions);
        Assert.DoesNotContain(harness.Upstream.Requests,
            request => request.RequestUri?.AbsolutePath == "/redirect-4");
        Assert.All(harness.Upstream.Requests,
            request => Assert.Equal("tile.openstreetmap.org", request.RequestUri?.Host));
    }

    /// <summary>
    /// Proves stale refresh charges the client on its first admitted contact, not its first loop attempt.
    /// </summary>
    [Fact]
    public async Task StaleRefresh_PreTransportRejectionChargesFirstActualContactOnce()
    {
        var contacts = 0;
        var upstream = new RecordingTileHandler((request, _) =>
        {
            contacts++;
            return Task.FromResult(contacts switch
            {
                1 => RedirectTo("/stale-redirect"),
                2 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => NotModifiedResponse()
            });
        });
        using var harness = new TileCacheTestHarness(upstream);
        harness.Settings.TileTrafficMode = TileTrafficMode.Conservative;
        SeedExpiredLowZoomTile(harness.CacheDirectory, 5, 23, 1, [9, 8, 7]);
        TileCacheService.SetRefreshRetryDelayForTesting(_ => TimeSpan.Zero);
        var acquisitions = 0;
        TileCacheService.OutboundBudget.SetAcquireOverrideForTesting(_ =>
            Task.FromResult(new OutboundBudgetAcquisition(
                Interlocked.Increment(ref acquisitions) != 1,
                TimeSpan.Zero)));
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);

        var result = Assert.IsType<FileContentResult>(await controller.GetTile(5, 23, 1));
        Assert.Equal(new byte[] { 9, 8, 7 }, result.FileContents);
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync(
            "5_23_1", TimeSpan.FromSeconds(2)));

        Assert.Equal(3, harness.Upstream.Requests.Count);
        var allowance = Assert.Single(TilesController.OutboundBudgetCache);
        Assert.Equal(1, allowance.Value.PeekCount(DateTime.UtcNow.Ticks));
    }

    /// <summary>Proves permanent stale revalidation responses stop without deleting stale bytes.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task StaleRefresh_Permanent4xxStopsAfterOneContact(HttpStatusCode statusCode)
    {
        using var harness = new TileCacheTestHarness(new RecordingTileHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))));
        var staleBytes = new byte[] { 4, 3, 2, 1 };
        var x = statusCode == HttpStatusCode.NotFound ? 24 : 25;
        SeedExpiredLowZoomTile(harness.CacheDirectory, 5, x, 1, staleBytes);
        var retryDelays = 0;
        TileCacheService.SetRefreshRetryDelayForTesting(_ =>
        {
            Interlocked.Increment(ref retryDelays);
            return TimeSpan.Zero;
        });
        using var scope = harness.CreateScope();
        var controller = CreateController(scope);
        var tileKey = $"5_{x}_1";

        var result = Assert.IsType<FileContentResult>(
            await controller.GetTile(5, x, 1));
        Assert.Equal(staleBytes, result.FileContents);
        Assert.True(await TileCacheService.WaitForRefreshIdleForTestingAsync(
            tileKey, TimeSpan.FromSeconds(2)));

        Assert.Single(harness.Upstream.Requests);
        Assert.Equal(0, retryDelays);
        Assert.Equal(staleBytes,
            await File.ReadAllBytesAsync(Path.Combine(harness.CacheDirectory, $"{tileKey}.png")));
    }

    /// <summary>Proves credentials cannot partition provider identity or its active safety gate.</summary>
    [Fact]
    public void ProviderFingerprint_ExcludesCredentialsAndNormalizesAuthority()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        TileProviderRetryPolicy.SetDeterminismForTesting(() => now, _ => 0d);
        const string firstCredential = "review-user";
        const string secondCredential = "review-password";
        var first = TileProviderRetryPolicy.GetProviderKey(
            $"https://{firstCredential}:{secondCredential}@BÜCHER.example/tiles/1.png");
        var equivalent = TileProviderRetryPolicy.GetProviderKey(
            "https://other:secret@xn--bcher-kva.example:443/other/2.png?apiKey=value");
        var differentPort = TileProviderRetryPolicy.GetProviderKey(
            "https://xn--bcher-kva.example:444/tiles/1.png");
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));

        TileProviderRetryPolicy.ApplyRetryAfter(first, response);

        Assert.Equal(first, equivalent);
        Assert.NotEqual(first, differentPort);
        Assert.Equal(TimeSpan.FromSeconds(20),
            TileProviderRetryPolicy.GetRemainingProviderDelay(equivalent));
        Assert.DoesNotContain(firstCredential, first, StringComparison.Ordinal);
        Assert.DoesNotContain(secondCredential, first, StringComparison.Ordinal);
    }

    /// <summary>Proves equivalent credential forms share one gate without entering diagnostics.</summary>
    [Fact]
    public async Task ProviderGateDiagnostics_ExcludeCredentialsAcrossEquivalentUrls()
    {
        const string firstCredential = "diagnostic-alice";
        const string secondCredential = "diagnostic-s3cret";
        var upstream = new RecordingTileHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        using var harness = new TileCacheTestHarness(upstream);
        using var firstScope = harness.CreateScope();
        firstScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            TileCacheTestHarness.CreateHttpContext();
        var firstService = firstScope.ServiceProvider.GetRequiredService<TileCacheService>();

        var first = await firstService.RetrieveTileAsync(
            "5",
            "26",
            "1",
            $"https://{firstCredential}:{secondCredential}@tiles.example.test/5/26/1.png");
        using var secondScope = harness.CreateScope();
        secondScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            TileCacheTestHarness.CreateHttpContext();
        var secondService = secondScope.ServiceProvider.GetRequiredService<TileCacheService>();
        var second = await secondService.RetrieveTileAsync(
            "5",
            "27",
            "1",
            "https://other:credential@tiles.example.test:443/5/27/1.png");

        Assert.Equal(TileRetrievalStatus.TransientFailure, first.Status);
        Assert.Equal(TileRetrievalStatus.TransientFailure, second.Status);
        Assert.Single(harness.Upstream.Requests);
        Assert.DoesNotContain(harness.Logs.Entries,
            entry => ContainsDiagnosticValue(entry, firstCredential));
        Assert.DoesNotContain(harness.Logs.Entries,
            entry => ContainsDiagnosticValue(entry, secondCredential));
    }

    /// <summary>Proves bounded normal gate operations eventually remove only expired providers.</summary>
    [Fact]
    public async Task ProviderGateCleanup_RemovesExpiredEntriesAndPreservesExtensions()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        TileProviderRetryPolicy.SetDeterminismForTesting(() => now, _ => 0d);
        for (var index = 0; index < 12; index++)
        {
            AddProviderGate($"https://tiles.example.test:{4400 + index}/tile.png", 1);
        }

        var activeKey = AddProviderGate("https://active.example.test/tile.png", 60);
        var extendedKey = AddProviderGate("https://extended.example.test/tile.png", 1);
        now = now.AddSeconds(2);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            if (index == 0)
            {
                AddProviderGate("https://extended.example.test/tile.png", 120);
            }
            else
            {
                TileProviderRetryPolicy.GetRemainingProviderDelay(activeKey);
            }
        })));

        for (var pass = 0; pass < 4; pass++)
        {
            TileProviderRetryPolicy.GetRemainingProviderDelay(activeKey);
        }

        Assert.Equal(2, GetProviderGateCount());
        Assert.True(TileProviderRetryPolicy.GetRemainingProviderDelay(activeKey) > TimeSpan.Zero);
        Assert.True(TileProviderRetryPolicy.GetRemainingProviderDelay(extendedKey) > TimeSpan.Zero);
    }

    /// <summary>Proves each live provider gate retains exactly one bounded cleanup record.</summary>
    [Fact]
    public async Task ProviderGateCleanup_KeepsOneQueueRecordPerDictionaryEntry()
    {
        TileCacheService.ResetStaticStateForTesting();
        try
        {
            var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
            TileProviderRetryPolicy.SetDeterminismForTesting(() => now, _ => 0d);
            var activeKeys = Enumerable.Range(0, 15)
                .Select(index => AddProviderGate(
                    $"https://active.example.test:{4400 + index}/tile.png",
                    3600))
                .ToArray();
            var extendedKey = AddProviderGate("https://extended.example.test/tile.png", 60);
            var expiredKey = AddProviderGate("https://expired.example.test/tile.png", 1);

            Assert.Equal(17, GetProviderGateCount());
            Assert.DoesNotContain(expiredKey, GetProviderGateCleanupKeys().Take(16));
            now = now.AddSeconds(2);

            for (var cycle = 0; cycle < 4; cycle++)
            {
                Assert.Equal(
                    TimeSpan.Zero,
                    TileProviderRetryPolicy.GetRemainingProviderDelay(expiredKey));
                Assert.Equal(GetProviderGateCount(), GetProviderGateCleanupKeys().Count);

                AddProviderGate("https://expired.example.test/tile.png", 1);
                Assert.Equal(GetProviderGateCount(), GetProviderGateCleanupKeys().Count);
                now = now.AddSeconds(2);
            }

            await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            {
                if (index == 0)
                {
                    AddProviderGate("https://extended.example.test/tile.png", 120);
                }
                else
                {
                    TileProviderRetryPolicy.GetRemainingProviderDelay(activeKeys[0]);
                }
            })));

            Assert.Equal(16, GetProviderGateCount());
            Assert.Equal(GetProviderGateCount(), GetProviderGateCleanupKeys().Count);
            Assert.True(TileProviderRetryPolicy.GetRemainingProviderDelay(activeKeys[0]) > TimeSpan.Zero);
            Assert.True(TileProviderRetryPolicy.GetRemainingProviderDelay(extendedKey) > TimeSpan.Zero);
        }
        finally
        {
            TileCacheService.ResetStaticStateForTesting();
        }
    }

    /// <summary>Proves provider templates cannot retain URI username or password data.</summary>
    [Fact]
    public void ProviderTemplateValidation_RejectsUserInformation()
    {
        var valid = TileProviderCatalog.TryValidateTemplate(
            "https://alice-review:s3cret-review@tiles.example.test/{z}/{x}/{y}.png",
            out var error);

        Assert.False(valid);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("alice-review", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s3cret-review", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates an owned same-host redirect response.</summary>
    private static HttpResponseMessage RedirectTo(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.Relative);
        return response;
    }

    /// <summary>Creates an owned 304 response with a bounded fresh lifetime.</summary>
    private static HttpResponseMessage NotModifiedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotModified);
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromHours(1)
        };
        return response;
    }

    /// <summary>Seeds an expired low-zoom tile without making any provider contact.</summary>
    private static void SeedExpiredLowZoomTile(
        string cacheDirectory,
        int zoom,
        int x,
        int y,
        byte[] bytes)
    {
        var tilePath = Path.Combine(cacheDirectory, $"{zoom}_{x}_{y}.png");
        File.WriteAllBytes(tilePath, bytes);
        File.WriteAllText(
            tilePath + ".meta",
            JsonSerializer.Serialize(new TileSidecarMetadata
            {
                ETag = "\"stale\"",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            }));
    }

    /// <summary>Adds or extends one provider gate and returns its non-secret key.</summary>
    private static string AddProviderGate(string tileUrl, int retryAfterSeconds)
    {
        var key = TileProviderRetryPolicy.GetProviderKey(tileUrl);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter =
            new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        TileProviderRetryPolicy.ApplyRetryAfter(key, response);
        return key;
    }

    /// <summary>Reads provider gate count for cleanup verification without exposing production state.</summary>
    private static int GetProviderGateCount()
    {
        var field = typeof(TileProviderRetryPolicy).GetField(
            "_providerNotBefore",
            BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsAssignableFrom<IDictionary>(field?.GetValue(null)).Count;
    }

    /// <summary>Reads the bounded cleanup records for exact ownership verification.</summary>
    private static IReadOnlyList<string> GetProviderGateCleanupKeys()
    {
        var field = typeof(TileProviderRetryPolicy).GetField(
            "_providerGateCleanupQueue",
            BindingFlags.NonPublic | BindingFlags.Static);
        return Assert.IsAssignableFrom<IEnumerable<string>>(field?.GetValue(null)).ToArray();
    }

    /// <summary>Checks every captured diagnostic surface for one supplied credential.</summary>
    private static bool ContainsDiagnosticValue(
        TestLogProvider.TestLogEntry entry,
        string value) =>
        entry.Message.Contains(value, StringComparison.Ordinal) ||
        entry.Fields.Values.Any(field =>
            field?.ToString()?.Contains(value, StringComparison.Ordinal) == true) ||
        entry.Exception?.ToString().Contains(value, StringComparison.Ordinal) == true;
}
