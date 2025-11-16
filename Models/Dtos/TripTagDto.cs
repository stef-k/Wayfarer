namespace Wayfarer.Models.Dtos;

/// <summary>
/// Simple DTO representing a tag currently attached to a trip.
/// </summary>
public sealed record TripTagDto(Guid Id, string Name, string Slug);

/// <summary>
/// Represents a tag suggestion/popularity item for public filtering.
/// </summary>
public sealed record TagSuggestionDto(string Name, string Slug, int Count);
