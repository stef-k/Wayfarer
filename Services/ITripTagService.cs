using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>
/// Service abstraction encapsulating tag CRUD, filtering, and suggestion logic.
/// </summary>
public interface ITripTagService
{
    Task<IReadOnlyList<TripTagDto>> GetTagsForTripAsync(Guid tripId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TripTagDto>> AttachTagsAsync(Guid tripId, IEnumerable<string> names, string userId, CancellationToken cancellationToken = default);

    Task<bool> DetachTagAsync(Guid tripId, string slug, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagSuggestionDto>> GetSuggestionsAsync(string? query, int limit = 10, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagSuggestionDto>> GetPopularAsync(int take = 20, CancellationToken cancellationToken = default);

    IQueryable<Trip> ApplyTagFilter(IQueryable<Trip> query, IReadOnlyCollection<string> slugs, string mode);

    Task RemoveOrphanTagsAsync(CancellationToken cancellationToken = default);
}
