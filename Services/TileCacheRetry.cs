using System.Net;
using Wayfarer.Services;

public partial class TileCacheService
{
    /// <summary>
    /// Executes the interim cold-tile retry policy without taking ownership of cache persistence.
    /// </summary>
    private async Task<TileDownloadResult> DownloadTileWithRetryAsync(
        string tileUrl,
        string? clientIp,
        bool allowHttpContext,
        CancellationToken cancellationToken)
    {
        var providerKey = TileProviderRetryPolicy.GetProviderKey(tileUrl);
        var interactiveDeadline = TileProviderRetryPolicy.UtcNow +
                                  TileProviderRetryPolicy.MaxInteractiveDuration;
        var contactState = new TileContactState();

        for (var attemptNumber = 1;
             attemptNumber <= TileProviderRetryPolicy.MaxAttempts;
             attemptNumber++)
        {
            var attemptRemaining = interactiveDeadline - TileProviderRetryPolicy.UtcNow;
            if (attemptRemaining <= TimeSpan.Zero)
            {
                return TileDownloadResult.Transient(TileProviderRetryPolicy.FallbackDelayCap);
            }

            using var attemptCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(attemptRemaining);
            TileRequestSendResult sendResult;
            try
            {
                sendResult = await SendTileRequestAsync(
                    tileUrl,
                    chargeClientAllowance: attemptNumber == 1,
                    clientIp: clientIp,
                    allowHttpContext: allowHttpContext,
                    attemptNumber: attemptNumber,
                    interactiveDeadline: interactiveDeadline,
                    contactState: contactState,
                    callerCancellationToken: cancellationToken,
                    cancellationToken: attemptCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                TileCacheDiagnostics.UpstreamClassification(
                    _logger, "transient-timeout", attemptNumber);
                if (contactState.IsExhausted)
                {
                    return TileDownloadResult.Transient(
                        TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
                }

                if (!await DelayForFallbackRetryAsync(
                        attemptNumber, interactiveDeadline, cancellationToken))
                {
                    return TileDownloadResult.Transient(
                        TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
                }

                continue;
            }
            catch (HttpRequestException)
            {
                TileCacheDiagnostics.UpstreamClassification(
                    _logger, "transient-transport", attemptNumber);
                if (contactState.IsExhausted)
                {
                    return TileDownloadResult.Transient(
                        TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
                }

                if (!await DelayForFallbackRetryAsync(
                        attemptNumber, interactiveDeadline, cancellationToken))
                {
                    return TileDownloadResult.Transient(
                        TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
                }

                continue;
            }

            if (sendResult.Rejection is TileRequestRejection.ClientBudget or
                TileRequestRejection.GlobalBudget)
            {
                _logger.LogWarning("Outbound tile budget rejected cache fill.");
                return TileDownloadResult.BudgetRejected();
            }

            if (sendResult.Rejection == TileRequestRejection.ProviderDeferred)
            {
                return TileDownloadResult.Transient(sendResult.RetryAfter);
            }

            if (sendResult.Rejection == TileRequestRejection.ContactLimit)
            {
                TileCacheDiagnostics.UpstreamClassification(
                    _logger, "transient-exhausted", attemptNumber);
                return TileDownloadResult.Transient(
                    TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
            }

            if (sendResult.Rejection == TileRequestRejection.InvalidProviderResponse)
            {
                TileCacheDiagnostics.UpstreamClassification(
                    _logger, "transient-invalid-response", attemptNumber);
                return TileDownloadResult.Transient(
                    TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
            }

            using var response = sendResult.Response!;
            if (response.IsSuccessStatusCode)
            {
                var tileData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                return TileDownloadResult.Downloaded(
                    tileData,
                    response.Headers.ETag?.Tag,
                    response.Content.Headers.LastModified?.UtcDateTime,
                    ParseCacheExpiry(response));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                TileCacheDiagnostics.UpstreamClassification(
                    _logger, "permanent-not-found", attemptNumber);
                return TileDownloadResult.NotFound();
            }

            if (IsPermanentUpstreamClientFailure(response.StatusCode))
            {
                TileCacheDiagnostics.UpstreamClassification(
                    _logger, "permanent-client-response", attemptNumber);
                return TileDownloadResult.PermanentFailure();
            }

            TileCacheDiagnostics.UpstreamClassification(
                _logger, "transient-response", attemptNumber);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or
                HttpStatusCode.ServiceUnavailable)
            {
                var providerDelay = TileProviderRetryPolicy.ApplyRetryAfter(providerKey, response);
                if (providerDelay.Kind == ProviderDelayKind.Valid)
                {
                    TileCacheDiagnostics.ProviderDelay(
                        _logger, "provider-directed", providerDelay.Delay.TotalMilliseconds);
                    var remaining = interactiveDeadline - TileProviderRetryPolicy.UtcNow;
                    if (attemptNumber >= TileProviderRetryPolicy.MaxAttempts ||
                        contactState.IsExhausted ||
                        providerDelay.Delay > remaining ||
                        providerDelay.Delay > TileProviderRetryPolicy.MaxIndividualWait)
                    {
                        return TileDownloadResult.Transient(providerDelay.Delay);
                    }

                    continue;
                }

                if (providerDelay.Kind == ProviderDelayKind.Invalid)
                {
                    TileCacheDiagnostics.ProviderDelay(
                        _logger, "invalid-provider-value", providerDelay.Delay.TotalMilliseconds);
                    _logger.LogError(
                        "Tile provider supplied an unusable Retry-After value; bounded provider safety gate opened.");
                    return TileDownloadResult.Transient(providerDelay.Delay);
                }
            }

            if (attemptNumber >= TileProviderRetryPolicy.MaxAttempts)
            {
                TileCacheDiagnostics.UpstreamClassification(
                    _logger, "transient-exhausted", attemptNumber);
                return TileDownloadResult.Transient(
                    TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
            }

            if (contactState.IsExhausted)
            {
                TileCacheDiagnostics.UpstreamClassification(
                    _logger, "transient-exhausted", attemptNumber);
                return TileDownloadResult.Transient(
                    TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
            }

            if (!await DelayForFallbackRetryAsync(
                    attemptNumber, interactiveDeadline, cancellationToken))
            {
                return TileDownloadResult.Transient(
                    TileProviderRetryPolicy.GetFallbackDelay(attemptNumber));
            }
        }

        return TileDownloadResult.Transient(TileProviderRetryPolicy.FallbackDelayCap);
    }

    /// <summary>Waits for one bounded fallback retry delay while preserving caller cancellation.</summary>
    private async Task<bool> DelayForFallbackRetryAsync(
        int failedAttempts,
        DateTimeOffset interactiveDeadline,
        CancellationToken cancellationToken)
    {
        if (failedAttempts >= TileProviderRetryPolicy.MaxAttempts)
        {
            return false;
        }

        var retryDelay = TileProviderRetryPolicy.GetFallbackDelay(failedAttempts);
        var remaining = interactiveDeadline - TileProviderRetryPolicy.UtcNow;
        if (remaining <= TimeSpan.Zero || retryDelay > remaining)
        {
            return false;
        }

        TileCacheDiagnostics.RetryDelaySelected(
            _logger,
            retryDelay.TotalMilliseconds,
            "fallback");
        try
        {
            await _coldMissRetryDelay(retryDelay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TileCacheDiagnostics.Cancellation(_logger, "cold-miss-retry-delay");
            throw;
        }
    }

    /// <summary>Identifies permanent provider client responses that must never be retried.</summary>
    private static bool IsPermanentUpstreamClientFailure(HttpStatusCode statusCode) =>
        (int)statusCode is >= 400 and < 500 &&
        statusCode is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests;
}
