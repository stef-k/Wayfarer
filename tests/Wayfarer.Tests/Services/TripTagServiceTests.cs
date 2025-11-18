using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for the TripTagService which handles tag creation, attachment, and management.
/// </summary>
public class TripTagServiceTests : TestBase
{
    private TripTagService CreateService()
    {
        var db = CreateDbContext();
        return new TripTagService(db, NullLogger<TripTagService>.Instance);
    }

    [Fact]
    public async Task GetTagsForTripAsync_ReturnsTagsForTrip()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);
        var tag1 = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };
        var tag2 = new Tag { Id = Guid.NewGuid(), Name = "Beach", Slug = "beach" };
        trip.Tags = new List<Tag> { tag1, tag2 };

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.Tags.AddRange(tag1, tag2);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        var result = await service.GetTagsForTripAsync(trip.Id, user.Id);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.Slug == "adventure");
        Assert.Contains(result, t => t.Slug == "beach");
    }

    [Fact]
    public async Task GetTagsForTripAsync_ThrowsForNonExistentTrip()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.GetTagsForTripAsync(Guid.NewGuid(), "user1"));
    }

    [Fact]
    public async Task GetTagsForTripAsync_ThrowsForWrongUser()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.GetTagsForTripAsync(trip.Id, "different-user"));
    }

    [Fact]
    public async Task AttachTagsAsync_CreatesNewTagAndAttaches()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        var result = await service.AttachTagsAsync(trip.Id, new[] { "Adventure" }, user.Id);

        // Assert
        Assert.Single(result);
        Assert.Equal("Adventure", result[0].Name);
        Assert.Equal("adventure", result[0].Slug);

        // Verify tag was created in database
        var dbTag = await db.Tags.FindAsync(result[0].Id);
        Assert.NotNull(dbTag);
    }

    [Fact]
    public async Task AttachTagsAsync_ReusesExistingTag()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);
        var existingTag = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.Tags.Add(existingTag);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        var result = await service.AttachTagsAsync(trip.Id, new[] { "Adventure" }, user.Id);

        // Assert
        Assert.Single(result);
        Assert.Equal(existingTag.Id, result[0].Id);
    }

    [Fact]
    public async Task AttachTagsAsync_SkipsDuplicateTags()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);
        var existingTag = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };
        trip.Tags = new List<Tag> { existingTag };

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.Tags.Add(existingTag);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        var result = await service.AttachTagsAsync(trip.Id, new[] { "Adventure" }, user.Id);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task AttachTagsAsync_ThrowsForEmptyTagList()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.AttachTagsAsync(trip.Id, Array.Empty<string>(), user.Id));
    }

    [Fact]
    public async Task AttachTagsAsync_ThrowsWhenExceedingMaxTags()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        // Add 24 existing tags
        trip.Tags = Enumerable.Range(1, 24)
            .Select(i => new Tag { Id = Guid.NewGuid(), Name = $"Tag{i}", Slug = $"tag{i}" })
            .ToList();

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.Tags.AddRange(trip.Tags);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act & Assert - Adding 2 more would exceed 25
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.AttachTagsAsync(trip.Id, new[] { "NewTag1", "NewTag2" }, user.Id));
    }

    [Fact]
    public async Task AttachTagsAsync_ValidatesTagName_RejectsEmpty()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act & Assert - Empty tags are filtered out, but if only empty provided, throws
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.AttachTagsAsync(trip.Id, new[] { "", "   " }, user.Id));
    }

    [Fact]
    public async Task AttachTagsAsync_ValidatesTagName_RejectsTooLong()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);
        var longTag = new string('a', 65);

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.AttachTagsAsync(trip.Id, new[] { longTag }, user.Id));
    }

    [Fact]
    public async Task AttachTagsAsync_ValidatesTagName_RejectsSpecialCharacters()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.AttachTagsAsync(trip.Id, new[] { "Tag@#$%" }, user.Id));
    }

    [Fact]
    public async Task AttachTagsAsync_AcceptsValidTagCharacters()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act - Valid characters: letters, numbers, spaces, hyphens, apostrophes
        var result = await service.AttachTagsAsync(trip.Id, new[] { "Road-Trip", "Sam's Journey", "Year 2024" }, user.Id);

        // Assert
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task DetachTagAsync_RemovesTagFromTrip()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };
        trip.Tags = new List<Tag> { tag };

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        var result = await service.DetachTagAsync(trip.Id, "adventure", user.Id);

        // Assert
        Assert.True(result);

        // Reload trip and verify tag is removed
        await db.Entry(trip).ReloadAsync();
        await db.Entry(trip).Collection(t => t.Tags).LoadAsync();
        Assert.Empty(trip.Tags);
    }

    [Fact]
    public async Task DetachTagAsync_DeletesOrphanedTag()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };
        trip.Tags = new List<Tag> { tag };

        db.Users.Add(user);
        db.Trips.Add(trip);
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        await service.DetachTagAsync(trip.Id, "adventure", user.Id);

        // Assert - Tag should be deleted since it's not used by any other trip
        var dbTag = await db.Tags.FindAsync(tag.Id);
        Assert.Null(dbTag);
    }

    [Fact]
    public async Task DetachTagAsync_KeepsTagUsedByOtherTrips()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip1 = TestDataFixtures.CreateTrip(user.Id);
        var trip2 = TestDataFixtures.CreateTrip(user.Id);
        var tag = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };
        trip1.Tags = new List<Tag> { tag };
        trip2.Tags = new List<Tag> { tag };

        db.Users.Add(user);
        db.Trips.AddRange(trip1, trip2);
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        await service.DetachTagAsync(trip1.Id, "adventure", user.Id);

        // Assert - Tag should still exist since trip2 uses it
        var dbTag = await db.Tags.FindAsync(tag.Id);
        Assert.NotNull(dbTag);
    }

    [Fact]
    public async Task DetachTagAsync_ReturnsFalseForNonExistentTag()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        var result = await service.DetachTagAsync(trip.Id, "nonexistent", user.Id);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DetachTagAsync_ReturnsFalseForEmptySlug()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act
        var result = await service.DetachTagAsync(Guid.NewGuid(), "", "user1");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ApplyTagFilter_WithAnySlugs_FiltersCorrectly()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var tag1 = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };
        var tag2 = new Tag { Id = Guid.NewGuid(), Name = "Beach", Slug = "beach" };
        var tag3 = new Tag { Id = Guid.NewGuid(), Name = "City", Slug = "city" };

        var trip1 = TestDataFixtures.CreateTrip(user.Id);
        trip1.Tags = new List<Tag> { tag1 };

        var trip2 = TestDataFixtures.CreateTrip(user.Id);
        trip2.Tags = new List<Tag> { tag2 };

        var trip3 = TestDataFixtures.CreateTrip(user.Id);
        trip3.Tags = new List<Tag> { tag3 };

        db.Users.Add(user);
        db.Tags.AddRange(tag1, tag2, tag3);
        db.Trips.AddRange(trip1, trip2, trip3);
        db.SaveChanges();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act - Filter for trips with "adventure" OR "beach"
        var query = db.Trips.AsQueryable();
        var filtered = service.ApplyTagFilter(query, new[] { "adventure", "beach" }, "any");
        var results = filtered.ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(results, t => t.Id == trip1.Id);
        Assert.Contains(results, t => t.Id == trip2.Id);
    }

    [Fact]
    public void ApplyTagFilter_WithAllSlugs_RequiresAllTags()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var tag1 = new Tag { Id = Guid.NewGuid(), Name = "Adventure", Slug = "adventure" };
        var tag2 = new Tag { Id = Guid.NewGuid(), Name = "Beach", Slug = "beach" };

        var trip1 = TestDataFixtures.CreateTrip(user.Id);
        trip1.Tags = new List<Tag> { tag1, tag2 };

        var trip2 = TestDataFixtures.CreateTrip(user.Id);
        trip2.Tags = new List<Tag> { tag1 };

        db.Users.Add(user);
        db.Tags.AddRange(tag1, tag2);
        db.Trips.AddRange(trip1, trip2);
        db.SaveChanges();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act - Filter for trips with "adventure" AND "beach"
        var query = db.Trips.AsQueryable();
        var filtered = service.ApplyTagFilter(query, new[] { "adventure", "beach" }, "all");
        var results = filtered.ToList();

        // Assert
        Assert.Single(results);
        Assert.Equal(trip1.Id, results[0].Id);
    }

    [Fact]
    public void ApplyTagFilter_WithEmptySlugs_ReturnsOriginalQuery()
    {
        // Arrange
        var db = CreateDbContext();
        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);
        var query = db.Trips.AsQueryable();

        // Act
        var filtered = service.ApplyTagFilter(query, Array.Empty<string>(), "any");

        // Assert - Should return same query
        Assert.Same(query, filtered);
    }

    [Fact]
    public async Task AttachTagsAsync_NormalizesTagNames()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act - Attach with extra whitespace
        var result = await service.AttachTagsAsync(trip.Id, new[] { "  Adventure  " }, user.Id);

        // Assert
        Assert.Single(result);
        Assert.Equal("Adventure", result[0].Name);
    }

    [Fact]
    public async Task AttachTagsAsync_DeduplicatesCaseInsensitive()
    {
        // Arrange
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser();
        var trip = TestDataFixtures.CreateTrip(user.Id);

        db.Users.Add(user);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        // Act - Same tag different cases
        var result = await service.AttachTagsAsync(trip.Id, new[] { "Adventure", "ADVENTURE", "adventure" }, user.Id);

        // Assert - Should only create one tag
        Assert.Single(result);
    }
}
