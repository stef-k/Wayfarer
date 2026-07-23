using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Characterizes the current cold-cache burst and timeout behavior without defining its permanent contract.
/// </summary>
[Collection("OutboundBudget")]
public sealed class TileCacheColdCacheBaselineTests
{
    private const int UniqueTileCount = 24;
    private static readonly TimeSpan FakeUpstreamLatency = TimeSpan.FromMilliseconds(100);
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the fixture with an output sink for the numeric baseline report.</summary>
    public TileCacheColdCacheBaselineTests(ITestOutputHelper output) => _output = output;

    /// <summary>Records the nominal current-policy burst, replenishment gaps, and local rejections.</summary>
    [Fact]
    public async Task ColdViewport_LargerThanBurst_RecordsCurrentBurstAndGapBaseline()
    {
        var budget = new ControlledCurrentBudget(
            TileCacheService.OutboundBudget.BurstCapacity,
            TimeSpan.FromMilliseconds(TileCacheService.OutboundBudget.ReplenishIntervalMs),
            TileCacheService.OutboundBudget.AcquireTimeout);
        var upstream = new RecordingTileHandler(startTimeProvider: budget.GetCurrentRequestStart);
        using var harness = new TileCacheTestHarness(upstream);
        TileCacheService.OutboundBudget.SetAcquireOverrideForTesting(budget.AcquireAsync);

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, UniqueTileCount)
                .Select(tileX => RequestTileAsync(harness, tileX)));
        var report = BuildReport(upstream.Requests, outcomes);

        _output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(24, report.UniqueTiles);
        Assert.Equal(12, report.BurstCapacity);
        Assert.Equal(2d, report.SustainedAcquisitionsPerSecond);
        Assert.Equal(100d, report.FakeUpstreamLatencyMilliseconds);
        Assert.Equal(19, report.AcceptedRequests);
        Assert.Equal(5, report.RejectedRequests);
        Assert.Equal(19, report.StatusDistribution[200]);
        Assert.Equal(5, report.StatusDistribution[503]);
        Assert.Equal(100d, report.FirstCompletionMilliseconds);
        Assert.Equal(3600d, report.LastCompletionMilliseconds);
        Assert.Equal(7, report.PeriodsWithoutProgress);
        Assert.Equal(500d, report.LongestPeriodWithoutProgressMilliseconds);
        Assert.All(outcomes.Where(outcome => outcome.StatusCode == 503),
            outcome => Assert.Equal(TilesController.BudgetRetryAfterSeconds, outcome.RetryAfterSeconds));
    }

    /// <summary>Runs one isolated controller request and captures its local status and Retry-After.</summary>
    private static async Task<LocalTileOutcome> RequestTileAsync(TileCacheTestHarness harness, int tileX)
    {
        using var scope = harness.CreateScope();
        var context = TileCacheTestHarness.CreateHttpContext();
        scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = context;
        var controller = new TilesController(
            scope.ServiceProvider.GetRequiredService<ILogger<TilesController>>(),
            scope.ServiceProvider.GetRequiredService<TileCacheService>(),
            scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var actionResult = await controller.GetTile(5, tileX, 1);
        var statusCode = actionResult switch
        {
            FileContentResult => 200,
            ObjectResult objectResult => objectResult.StatusCode ?? 200,
            StatusCodeResult statusResult => statusResult.StatusCode,
            _ => throw new InvalidOperationException($"Unexpected tile result {actionResult.GetType().Name}.")
        };
        var retryAfter = int.TryParse(context.Response.Headers.RetryAfter, out var parsedRetryAfter)
            ? parsedRetryAfter
            : (int?)null;

        return new LocalTileOutcome(tileX, statusCode, retryAfter);
    }

    /// <summary>Derives a stable numerical report from fake-upstream starts and local controller outcomes.</summary>
    private static ColdCacheBaselineReport BuildReport(
        IReadOnlyCollection<RecordedTileRequest> requests,
        IReadOnlyCollection<LocalTileOutcome> outcomes)
    {
        var successfulCompletions = requests
            .Select(request => request.StartTime + FakeUpstreamLatency)
            .OrderBy(completion => completion)
            .ToArray();
        var distinctCompletions = successfulCompletions.Distinct().ToArray();
        var progressGaps = distinctCompletions
            .Zip(distinctCompletions.Skip(1), (first, second) => second - first)
            .Where(gap => gap > FakeUpstreamLatency)
            .ToArray();
        var rejectedCompletion = TileCacheService.OutboundBudget.AcquireTimeout;
        var lastCompletion = successfulCompletions
            .Append(rejectedCompletion)
            .Max();

        return new ColdCacheBaselineReport(
            UniqueTiles: outcomes.Count,
            BurstCapacity: TileCacheService.OutboundBudget.BurstCapacity,
            SustainedAcquisitionsPerSecond:
                1000d / TileCacheService.OutboundBudget.ReplenishIntervalMs,
            FakeUpstreamLatencyMilliseconds: FakeUpstreamLatency.TotalMilliseconds,
            AcceptedRequests: requests.Count,
            RejectedRequests: outcomes.Count(outcome => outcome.StatusCode == 503),
            StatusDistribution: outcomes
                .GroupBy(outcome => outcome.StatusCode)
                .ToDictionary(group => group.Key, group => group.Count()),
            UpstreamStartMilliseconds: requests
                .Select(request => request.StartTime.TotalMilliseconds)
                .OrderBy(start => start)
                .ToArray(),
            RetryAfterSeconds: outcomes
                .Where(outcome => outcome.RetryAfterSeconds.HasValue)
                .Select(outcome => outcome.RetryAfterSeconds!.Value)
                .ToArray(),
            FirstCompletionMilliseconds: successfulCompletions.Min().TotalMilliseconds,
            LastCompletionMilliseconds: lastCompletion.TotalMilliseconds,
            PeriodsWithoutProgress: progressGaps.Length,
            LongestPeriodWithoutProgressMilliseconds: progressGaps.Max().TotalMilliseconds);
    }

    /// <summary>
    /// Deterministically reproduces the current burst, replenishment, and acquisition-timeout constants.
    /// </summary>
    private sealed class ControlledCurrentBudget
    {
        private readonly int _burstCapacity;
        private readonly TimeSpan _replenishmentInterval;
        private readonly TimeSpan _acquireTimeout;
        private readonly ConcurrentQueue<TimeSpan> _acceptedRequestStarts = new();
        private int _requestSequence;

        public ControlledCurrentBudget(
            int burstCapacity,
            TimeSpan replenishmentInterval,
            TimeSpan acquireTimeout)
        {
            _burstCapacity = burstCapacity;
            _replenishmentInterval = replenishmentInterval;
            _acquireTimeout = acquireTimeout;
        }

        /// <summary>Assigns the nominal current-policy acquisition time for the next request.</summary>
        public Task<OutboundBudgetAcquisition> AcquireAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = Interlocked.Increment(ref _requestSequence);
            var wait = sequence <= _burstCapacity
                ? TimeSpan.Zero
                : _replenishmentInterval * (sequence - _burstCapacity);
            var acquired = wait <= _acquireTimeout;
            if (acquired)
            {
                _acceptedRequestStarts.Enqueue(wait);
            }
            return Task.FromResult(new OutboundBudgetAcquisition(
                acquired,
                acquired ? wait : _acquireTimeout));
        }

        /// <summary>Gets the logical upstream start assigned to the current asynchronous request.</summary>
        public TimeSpan GetCurrentRequestStart() =>
            _acceptedRequestStarts.TryDequeue(out var start)
                ? start
                : throw new InvalidOperationException("No controlled acquisition was assigned to this request.");
    }

    /// <summary>Captures the local controller result for one unique visible tile.</summary>
    private sealed record LocalTileOutcome(int TileX, int StatusCode, int? RetryAfterSeconds);

    /// <summary>Contains all numeric evidence required to reproduce the controlled baseline.</summary>
    private sealed record ColdCacheBaselineReport(
        int UniqueTiles,
        int BurstCapacity,
        double SustainedAcquisitionsPerSecond,
        double FakeUpstreamLatencyMilliseconds,
        int AcceptedRequests,
        int RejectedRequests,
        IReadOnlyDictionary<int, int> StatusDistribution,
        IReadOnlyList<double> UpstreamStartMilliseconds,
        IReadOnlyList<int> RetryAfterSeconds,
        double FirstCompletionMilliseconds,
        double LastCompletionMilliseconds,
        int PeriodsWithoutProgress,
        double LongestPeriodWithoutProgressMilliseconds);
}
