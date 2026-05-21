namespace Wayfarer.Services;

/// <summary>
/// Builds PDF cover snapshot data URIs through the shared image proxy pipeline.
/// </summary>
internal static class TripExportCoverSnapshotBuilder
{
    /// <summary>
    /// Fetches the cover image through the proxy service and returns a complete data URI.
    /// </summary>
    public static async Task<string?> BuildDataUriAsync(
        IImageProxyService imageProxyService,
        string? coverImageUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(coverImageUrl))
        {
            return null;
        }

        try
        {
            var result = await imageProxyService.GetOrFetchAsync(
                new ImageProxyRequest(coverImageUrl),
                allowOriginFetch: true,
                cancellationToken);

            if (!result.HasBytes)
            {
                return null;
            }

            return $"data:{result.ContentType};base64,{Convert.ToBase64String(result.Bytes!)}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
