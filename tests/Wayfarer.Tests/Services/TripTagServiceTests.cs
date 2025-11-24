using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="TripTagService"/>.
/// </summary>
public class TripTagServiceTests : TestBase
{
    [Fact]
    public async Task GetTagsForTripAsync_Throws_WhenTripMissing()
    {
        var db = CreateDbContext();
        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.GetTagsForTripAsync(Guid.NewGuid(), "user"));
    }

    [Fact]
    public async Task GetTagsForTripAsync_ReturnsOrderedTags()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u", Name = "Trip" };
        var tagB = new Tag { Id = Guid.NewGuid(), Name = "beta", Slug = "beta" };
        var tagA = new Tag { Id = Guid.NewGuid(), Name = "alpha", Slug = "alpha" };
        trip.Tags.Add(tagB);
        trip.Tags.Add(tagA);
        db.Trips.Add(trip);
        db.Tags.AddRange(tagA, tagB);
        await db.SaveChangesAsync();

        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        var result = await service.GetTagsForTripAsync(trip.Id, "u");

        Assert.Equal(new[] { "alpha", "beta" }, result.Select(t => t.Name));
    }

    [Fact]
    public async Task AttachTagsAsync_AddsNewDistinctTags_WithSlugNormalization()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u", Name = "Trip" };
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        var result = await service.AttachTagsAsync(trip.Id, new[] { "Café", "cafe", " Unique ", "unique" }, "u");

        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Select(t => t.Slug).Distinct().Count());
        Assert.Contains(result, t => t.Slug == "cafe");
        Assert.Contains(result, t => t.Slug == "unique");
    }

    [Fact]
    public async Task AttachTagsAsync_Throws_WhenExceedingMax()
    {
        var db = CreateDbContext();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u", Name = "Trip" };
        db.Trips.Add(trip);
        for (int i = 0; i < 25; i++)
        {
            var tag = new Tag { Id = Guid.NewGuid(), Name = $"tag{i}", Slug = $"tag{i}" };
            trip.Tags.Add(tag);
            db.Tags.Add(tag);
        }
        await db.SaveChangesAsync();
        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.AttachTagsAsync(trip.Id, new[] { "overflow" }, "u"));
    }

    [Fact]
    public async Task DetachTagAsync_RemovesTagAndDeletesOrphan()
    {
        var db = CreateDbContext();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "tag", Slug = "tag" };
        var trip = new Trip { Id = Guid.NewGuid(), UserId = "u", Name = "Trip", Tags = new List<Tag> { tag } };
        tag.Trips.Add(trip);
        db.Trips.Add(trip);
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        var removed = await service.DetachTagAsync(trip.Id, "tag", "u");

        Assert.True(removed);
        Assert.Empty(db.Trips.Include(t => t.Tags).Single(t => t.Id == trip.Id).Tags);
        Assert.Empty(db.Tags);
    }

    [Fact]
    public async Task ApplyTagFilter_FiltersAllOrAny()
    {
        var db = CreateDbContext();
        var tagA = new Tag { Id = Guid.NewGuid(), Name = "A", Slug = "a" };
        var tagB = new Tag { Id = Guid.NewGuid(), Name = "B", Slug = "b" };
        var trip1 = new Trip { Id = Guid.NewGuid(), UserId = "u", Name = "T1", Tags = new List<Tag> { tagA, tagB } };
        var trip2 = new Trip { Id = Guid.NewGuid(), UserId = "u", Name = "T2", Tags = new List<Tag> { tagA } };
        tagA.Trips.Add(trip1); tagA.Trips.Add(trip2); tagB.Trips.Add(trip1);
        db.AddRange(tagA, tagB, trip1, trip2);
        await db.SaveChangesAsync();
        var service = new TripTagService(db, NullLogger<TripTagService>.Instance);

        var allQuery = service.ApplyTagFilter(db.Trips.Include(t => t.Tags), new[] { "a", "b" }, "all").ToList();
        var anyQuery = service.ApplyTagFilter(db.Trips.Include(t => t.Tags), new[] { "a", "b" }, "any").ToList();

        Assert.Single(allQuery);
        Assert.Equal(trip1.Id, allQuery[0].Id);
        Assert.Equal(2, anyQuery.Count);
    }
}
