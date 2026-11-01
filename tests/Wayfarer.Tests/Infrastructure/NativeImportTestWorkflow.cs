using Wayfarer.Services;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Existing import cases use the shipped read-review-apply sequence for native replacement.</summary>
internal static class NativeImportTestWorkflow
{
    /// <summary>Keeps explicit modes and assertions intact; reviewed updates default to Keep existing.</summary>
    internal static async Task<TripImportResult> ImportWithReviewAsync(this ITripImportService service,
        Stream file, string owner, TripImportMode mode, CancellationToken token = default)
    {
        if (mode != TripImportMode.Upsert) return await service.ImportWayfarerKmlAsync(file, owner, mode, token);
        var position = file.Position;
        var review = await service.ReviewNativeAsync(file, owner, token);
        file.Position = position;
        return await service.ImportReviewedNativeAsync(file, owner, new(review.Token, []), token);
    }
}
