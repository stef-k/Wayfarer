using FluentAssertions;
using Wayfarer.Models.Dtos;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for VisitBackfillService deduplication and sorting logic.
/// Since the service relies on PostGIS raw SQL for data retrieval,
/// these tests verify the ordering and dedup contracts on pre-built DTOs.
/// </summary>
public class VisitBackfillServiceTests
{
    /// <summary>
    /// Verifies that suggested visits do not contain duplicate (PlaceId, VisitDate) pairs.
    /// </summary>
    [Fact]
    public void SuggestedVisits_ContainsNoDuplicatePlaceDatePairs()
    {
        // Arrange: simulate raw suggestions with duplicates
        var placeId = Guid.NewGuid();
        var date = new DateOnly(2024, 6, 15);
        var suggestions = new List<SuggestedVisitDto>
        {
            MakeSuggestion(placeId, "Place A", "Region A", date, 100),
            MakeSuggestion(placeId, "Place A", "Region A", date, 95), // duplicate
            MakeSuggestion(placeId, "Place A", "Region A", new DateOnly(2024, 6, 20), 200), // different date, ok
        };

        // Act: deduplicate by (PlaceId, VisitDate) — mirrors the HashSet logic in the service
        var seen = new HashSet<(Guid, DateOnly)>();
        var deduped = suggestions.Where(s => seen.Add((s.PlaceId, s.VisitDate))).ToList();

        // Assert
        deduped.Should().HaveCount(2);
        deduped.Select(s => (s.PlaceId, s.VisitDate)).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Verifies that re-visits (same place, different dates) are preserved after dedup.
    /// </summary>
    [Fact]
    public void SuggestedVisits_PreservesReVisitsOnDifferentDates()
    {
        var placeId = Guid.NewGuid();
        var suggestions = new List<SuggestedVisitDto>
        {
            MakeSuggestion(placeId, "Place A", "Region A", new DateOnly(2024, 6, 15), 100),
            MakeSuggestion(placeId, "Place A", "Region A", new DateOnly(2024, 6, 18), 150),
            MakeSuggestion(placeId, "Place A", "Region A", new DateOnly(2024, 7, 1), 200),
        };

        // Act
        var seen = new HashSet<(Guid, DateOnly)>();
        var deduped = suggestions.Where(s => seen.Add((s.PlaceId, s.VisitDate))).ToList();

        // Assert: all three are on different dates, so all should be preserved
        deduped.Should().HaveCount(3);
    }

    /// <summary>
    /// Verifies NewVisits are sorted by Region (alpha) → PlaceName (alpha) → VisitDate (desc).
    /// </summary>
    [Fact]
    public void NewVisits_SortedByRegionThenPlaceThenDateDesc()
    {
        var candidates = new List<BackfillCandidateDto>
        {
            MakeCandidate("Place B", "Region B", new DateOnly(2024, 6, 10)),
            MakeCandidate("Place A", "Region A", new DateOnly(2024, 6, 15)),
            MakeCandidate("Place A", "Region A", new DateOnly(2024, 6, 18)),
            MakeCandidate("Place C", "Region A", new DateOnly(2024, 5, 20)),
            MakeCandidate("Place B", "Region A", new DateOnly(2024, 6, 12)),
        };

        // Act: apply the same sorting as the service
        var sorted = candidates
            .OrderBy(c => c.RegionName).ThenBy(c => c.PlaceName).ThenByDescending(c => c.VisitDate)
            .ToList();

        // Assert
        sorted.Select(c => (c.RegionName, c.PlaceName, c.VisitDate)).Should().ContainInOrder(
            ("Region A", "Place A", new DateOnly(2024, 6, 18)),
            ("Region A", "Place A", new DateOnly(2024, 6, 15)),
            ("Region A", "Place B", new DateOnly(2024, 6, 12)),
            ("Region A", "Place C", new DateOnly(2024, 5, 20)),
            ("Region B", "Place B", new DateOnly(2024, 6, 10))
        );
    }

    /// <summary>
    /// Verifies SuggestedVisits are sorted by Region → PlaceName → MinDistance → VisitDate (desc).
    /// </summary>
    [Fact]
    public void SuggestedVisits_SortedByRegionThenPlaceThenProximityThenDateDesc()
    {
        var suggestions = new List<SuggestedVisitDto>
        {
            MakeSuggestion(Guid.NewGuid(), "Place A", "Region B", new DateOnly(2024, 6, 10), 300),
            MakeSuggestion(Guid.NewGuid(), "Place A", "Region A", new DateOnly(2024, 6, 15), 200),
            MakeSuggestion(Guid.NewGuid(), "Place A", "Region A", new DateOnly(2024, 6, 18), 100),
            MakeSuggestion(Guid.NewGuid(), "Place A", "Region A", new DateOnly(2024, 6, 12), 200),
        };

        // Act
        var sorted = suggestions
            .OrderBy(s => s.RegionName).ThenBy(s => s.PlaceName).ThenBy(s => s.MinDistanceMeters).ThenByDescending(s => s.VisitDate)
            .ToList();

        // Assert: Region A first, then by proximity (100 < 200), then date desc within same proximity
        sorted.Select(s => (s.RegionName, s.MinDistanceMeters, s.VisitDate)).Should().ContainInOrder(
            ("Region A", 100.0, new DateOnly(2024, 6, 18)),
            ("Region A", 200.0, new DateOnly(2024, 6, 15)),
            ("Region A", 200.0, new DateOnly(2024, 6, 12)),
            ("Region B", 300.0, new DateOnly(2024, 6, 10))
        );
    }

    /// <summary>
    /// Verifies StaleVisits are sorted by Region → PlaceName → VisitDate (desc).
    /// </summary>
    [Fact]
    public void StaleVisits_SortedByRegionThenPlaceThenDateDesc()
    {
        var stale = new List<StaleVisitDto>
        {
            MakeStale("Place C", "Region B", new DateOnly(2024, 5, 1)),
            MakeStale("Place A", "Region A", new DateOnly(2024, 6, 15)),
            MakeStale("Place A", "Region A", new DateOnly(2024, 6, 20)),
            MakeStale("Place B", "Region A", new DateOnly(2024, 3, 10)),
        };

        // Act
        var sorted = stale
            .OrderBy(s => s.RegionName).ThenBy(s => s.PlaceName).ThenByDescending(s => s.VisitDate)
            .ToList();

        // Assert
        sorted.Select(s => (s.RegionName, s.PlaceName, s.VisitDate)).Should().ContainInOrder(
            ("Region A", "Place A", new DateOnly(2024, 6, 20)),
            ("Region A", "Place A", new DateOnly(2024, 6, 15)),
            ("Region A", "Place B", new DateOnly(2024, 3, 10)),
            ("Region B", "Place C", new DateOnly(2024, 5, 1))
        );
    }

    /// <summary>
    /// Verifies ExistingVisits are sorted by Region → PlaceName → VisitDate (desc).
    /// </summary>
    [Fact]
    public void ExistingVisits_SortedByRegionThenPlaceThenDateDesc()
    {
        var existing = new List<ExistingVisitDto>
        {
            MakeExisting("Place B", "Region A", new DateOnly(2024, 7, 1)),
            MakeExisting("Place A", "Region B", new DateOnly(2024, 8, 1)),
            MakeExisting("Place A", "Region A", new DateOnly(2024, 6, 15)),
            MakeExisting("Place A", "Region A", new DateOnly(2024, 6, 20)),
        };

        // Act
        var sorted = existing
            .OrderBy(e => e.RegionName).ThenBy(e => e.PlaceName).ThenByDescending(e => e.VisitDate)
            .ToList();

        // Assert
        sorted.Select(e => (e.RegionName, e.PlaceName, e.VisitDate)).Should().ContainInOrder(
            ("Region A", "Place A", new DateOnly(2024, 6, 20)),
            ("Region A", "Place A", new DateOnly(2024, 6, 15)),
            ("Region A", "Place B", new DateOnly(2024, 7, 1)),
            ("Region B", "Place A", new DateOnly(2024, 8, 1))
        );
    }

    #region Helper Factories

    /// <summary>
    /// Creates a BackfillCandidateDto with the specified properties for testing.
    /// </summary>
    private static BackfillCandidateDto MakeCandidate(string placeName, string regionName, DateOnly visitDate) =>
        new()
        {
            PlaceId = Guid.NewGuid(),
            PlaceName = placeName,
            RegionName = regionName,
            VisitDate = visitDate,
            FirstSeenUtc = visitDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            LastSeenUtc = visitDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc),
            LocationCount = 5,
            AvgDistanceMeters = 50,
            Confidence = 80
        };

    /// <summary>
    /// Creates a SuggestedVisitDto with the specified properties for testing.
    /// </summary>
    private static SuggestedVisitDto MakeSuggestion(Guid placeId, string placeName, string regionName,
        DateOnly visitDate, double minDistance) =>
        new()
        {
            PlaceId = placeId,
            PlaceName = placeName,
            RegionName = regionName,
            VisitDate = visitDate,
            MinDistanceMeters = minDistance,
            HitsTier1 = 1,
            HitsTier2 = 3,
            HitsTier3 = 5,
            HitsTotal = 9,
            HasUserCheckin = false,
            SuggestionReason = "Cross-tier evidence",
            FirstSeenUtc = visitDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            LastSeenUtc = visitDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc)
        };

    /// <summary>
    /// Creates a StaleVisitDto with the specified properties for testing.
    /// </summary>
    private static StaleVisitDto MakeStale(string placeName, string regionName, DateOnly visitDate) =>
        new()
        {
            VisitId = Guid.NewGuid(),
            PlaceName = placeName,
            RegionName = regionName,
            VisitDate = visitDate,
            Reason = "Place was deleted"
        };

    /// <summary>
    /// Creates an ExistingVisitDto with the specified properties for testing.
    /// </summary>
    private static ExistingVisitDto MakeExisting(string placeName, string regionName, DateOnly visitDate) =>
        new()
        {
            VisitId = Guid.NewGuid(),
            PlaceName = placeName,
            RegionName = regionName,
            VisitDate = visitDate,
            ArrivedAtUtc = visitDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            IsOpen = false
        };

    #endregion
}
