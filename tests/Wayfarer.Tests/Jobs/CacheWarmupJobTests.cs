using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Jobs;

/// <summary>
/// Tests for <see cref="CacheWarmupJob"/>: URL extraction from notes/cover images,
/// handling of relative/data URIs, and non-existent trips.
/// </summary>
public class CacheWarmupJobTests : TestBase
{
    [Fact]
    public async Task Execute_ExtractsAndCachesNotesImages()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        var regionId = Guid.NewGuid();

        db.Trips.Add(new Trip
        {
            Id = tripId,
            UserId = "user1",
            Name = "Test Trip",
            Notes = "<p>Hello <img src=\"https://example.com/photo1.jpg\"> and <img src='https://cdn.test.com/photo2.png'></p>",
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    UserId = "user1",
                    TripId = tripId,
                    Name = "Region 1",
                    Places = new List<Place>(),
                    Areas = new List<Area>()
                }
            }
        });
        await db.SaveChangesAsync();

        var proxyMock = new Mock<IImageProxyService>();
        proxyMock.Setup(p => p.FetchAndCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var job = new CacheWarmupJob(db, proxyMock.Object, NullLogger<CacheWarmupJob>.Instance);
        var context = CreateJobContext(tripId);

        await job.Execute(context);

        // Should have been called for both image URLs
        proxyMock.Verify(p => p.FetchAndCacheAsync("https://example.com/photo1.jpg", It.IsAny<CancellationToken>()), Times.Once);
        proxyMock.Verify(p => p.FetchAndCacheAsync("https://cdn.test.com/photo2.png", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_CachesCoverImageUrl()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        var regionId = Guid.NewGuid();

        db.Trips.Add(new Trip
        {
            Id = tripId,
            UserId = "user1",
            Name = "Cover Trip",
            CoverImageUrl = "https://example.com/cover.jpg",
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    UserId = "user1",
                    TripId = tripId,
                    Name = "Region 1",
                    CoverImageUrl = "https://example.com/region-cover.jpg",
                    Places = new List<Place>(),
                    Areas = new List<Area>()
                }
            }
        });
        await db.SaveChangesAsync();

        var proxyMock = new Mock<IImageProxyService>();
        proxyMock.Setup(p => p.FetchAndCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var job = new CacheWarmupJob(db, proxyMock.Object, NullLogger<CacheWarmupJob>.Instance);
        var context = CreateJobContext(tripId);

        await job.Execute(context);

        proxyMock.Verify(p => p.FetchAndCacheAsync("https://example.com/cover.jpg", It.IsAny<CancellationToken>()), Times.Once);
        proxyMock.Verify(p => p.FetchAndCacheAsync("https://example.com/region-cover.jpg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_SkipsRelativeAndDataUrls()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        var regionId = Guid.NewGuid();

        db.Trips.Add(new Trip
        {
            Id = tripId,
            UserId = "user1",
            Name = "Local Trip",
            Notes = "<p><img src=\"/local.jpg\"> <img src=\"data:image/png;base64,abc123\"></p>",
            Regions = new List<Region>
            {
                new Region
                {
                    Id = regionId,
                    UserId = "user1",
                    TripId = tripId,
                    Name = "Region 1",
                    Places = new List<Place>(),
                    Areas = new List<Area>()
                }
            }
        });
        await db.SaveChangesAsync();

        var proxyMock = new Mock<IImageProxyService>();

        var job = new CacheWarmupJob(db, proxyMock.Object, NullLogger<CacheWarmupJob>.Instance);
        var context = CreateJobContext(tripId);

        await job.Execute(context);

        // Neither relative nor data: URLs should be passed to FetchAndCacheAsync
        proxyMock.Verify(p => p.FetchAndCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_HandlesNonExistentTrip()
    {
        var db = CreateDbContext();
        var proxyMock = new Mock<IImageProxyService>();

        var job = new CacheWarmupJob(db, proxyMock.Object, NullLogger<CacheWarmupJob>.Instance);
        var context = CreateJobContext(Guid.NewGuid()); // Non-existent trip

        // Should complete without error
        await job.Execute(context);

        proxyMock.Verify(p => p.FetchAndCacheAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Creates a mock <see cref="IJobExecutionContext"/> with the given tripId in the JobDataMap.
    /// </summary>
    private static IJobExecutionContext CreateJobContext(Guid tripId)
    {
        var jobDataMap = new JobDataMap { { "tripId", tripId.ToString() } };

        var jobDetail = new Mock<IJobDetail>();
        jobDetail.Setup(j => j.JobDataMap).Returns(jobDataMap);

        var context = new Mock<IJobExecutionContext>();
        context.Setup(c => c.JobDetail).Returns(jobDetail.Object);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);

        return context.Object;
    }
}
