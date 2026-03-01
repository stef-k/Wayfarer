using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Public trip viewer behaviors: index search/pagination, view gating, preview, thumbnail proxy.
/// </summary>
public class TripViewerControllerTests : TestBase
{
    [Fact]
    public async Task View_ReturnsNotFound_WhenPrivate()
    {
        var db = CreateDbContext();
        db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = "u1", Name = "Private", IsPublic = false });
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.View(db.Trips.First().Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Preview_ReturnsNotFound_WhenMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.Preview(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task View_ReturnsView_ForPublicTrip()
    {
        var db = CreateDbContext();
        var tripId = Guid.NewGuid();
        db.Users.Add(TestDataFixtures.CreateUser(id: "owner", username: "owner"));
        db.Trips.Add(new Trip { Id = tripId, UserId = "owner", Name = "Public", IsPublic = true, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var controller = BuildController(db);

        var result = await controller.View(tripId);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<Trip>(view.Model);
        Assert.Equal(tripId, model.Id);
    }

    [Fact]
    public async Task Preview_ReturnsPartial_ForPublicTrip()
    {
        var db = CreateDbContext();
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = "u1",
            Name = "Trip",
            IsPublic = true,
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>(),
            Segments = new List<Segment>(),
            Tags = new List<Tag>()
        };
        db.Trips.Add(trip);
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1"));
        await db.SaveChangesAsync();
        var thumbSvc = new Mock<ITripThumbnailService>();
        thumbSvc.Setup(s => s.GetThumbUrlAsync(trip.Id, trip.CenterLat, trip.CenterLon, trip.Zoom, trip.CoverImageUrl, trip.UpdatedAt, "800x450", It.IsAny<CancellationToken>()))
            .ReturnsAsync("thumb");
        var controller = BuildController(db, thumbnailService: thumbSvc.Object);

        var result = await controller.Preview(trip.Id);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("~/Areas/Public/Views/TripViewer/_TripQuickView.cshtml", partial.ViewName);
        var model = Assert.IsType<PublicTripIndexItem>(partial.Model);
        Assert.Equal("thumb", model.ThumbUrl);
    }

    [Fact]
    public async Task GetThumbnail_ReturnsJson_WhenFound()
    {
        var db = CreateDbContext();
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = "u1",
            Name = "Trip",
            IsPublic = true,
            UpdatedAt = DateTime.UtcNow
        };
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        var thumbSvc = new Mock<ITripThumbnailService>();
        thumbSvc.Setup(s => s.GetThumbUrlAsync(trip.Id, trip.CenterLat, trip.CenterLon, trip.Zoom, trip.CoverImageUrl, trip.UpdatedAt, "800x450", It.IsAny<CancellationToken>()))
            .ReturnsAsync("thumb-url");
        var controller = BuildController(db, thumbnailService: thumbSvc.Object);

        var result = await controller.GetThumbnail(trip.Id, "800x450");

        var json = Assert.IsType<JsonResult>(result);
        var thumb = json.Value?.GetType().GetProperty("thumbUrl")?.GetValue(json.Value)?.ToString();
        Assert.Equal("thumb-url", thumb);
    }

    [Fact]
    public async Task GetThumbnail_ReturnsNotFound_WhenMissing()
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.GetThumbnail(Guid.NewGuid(), "800x450");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Index_ReturnsPublicTrips_WithPagingAndDefaults()
    {
        var db = CreateDbContext();
        var publicTrip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = "u1",
            Name = "Public Trip",
            IsPublic = true,
            UpdatedAt = DateTime.UtcNow,
            Regions = new List<Region>(),
            Segments = new List<Segment>(),
            Tags = new List<Tag>()
        };
        var privateTrip = new Trip { Id = Guid.NewGuid(), UserId = "u2", Name = "Private Trip", IsPublic = false, UpdatedAt = DateTime.UtcNow };
        db.Trips.AddRange(publicTrip, privateTrip);
        db.Users.Add(TestDataFixtures.CreateUser(id: "u1", username: "alice"));
        await db.SaveChangesAsync();

        var tagService = new Mock<ITripTagService>();
        tagService.Setup(s => s.ApplyTagFilter(It.IsAny<IQueryable<Trip>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>()))
            .Returns<IQueryable<Trip>, IReadOnlyCollection<string>, string>((q, tags, mode) => q);
        tagService.Setup(s => s.GetPopularAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TagSuggestionDto>());

        var controller = BuildController(db, tagService: tagService.Object);

        var result = await controller.Index(q: null, view: null, sort: null, tags: null!, tagMode: null, page: 1, pageSize: 24);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PublicTripIndexVm>(view.Model);
        Assert.Single(model.Items);
        Assert.Equal("grid", model.View);
        Assert.Equal("updated_desc", model.Sort);
        Assert.Equal(1, model.Page);
        Assert.Equal(24, model.PageSize);
    }

    [Fact]
    public void ByTag_RedirectsToIndexRoute()
    {
        var controller = BuildController(CreateDbContext());

        var result = controller.ByTag("hiking", view: "grid", sort: "name_asc", page: 2);

        var redirect = Assert.IsType<RedirectToRouteResult>(result);
        Assert.Equal("PublicTripsIndex", redirect.RouteName);
        Assert.Equal("hiking", redirect.RouteValues!["tags"]);
        Assert.Equal(2, redirect.RouteValues!["page"]);
    }

    [Fact]
    public async Task ProxyImage_ReturnsStatusCode_FromHttpClient()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var controller = BuildController(CreateDbContext(), handler: handler);

        var result = await controller.ProxyImage("http://example.com/img.png");

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1/secret")]
    [InlineData("http://localhost/secret")]
    [InlineData("http://10.0.0.1/secret")]
    [InlineData("http://192.168.1.1/secret")]
    [InlineData("http://172.16.0.1/secret")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    public async Task ProxyImage_ReturnsBadRequest_ForDisallowedUrls(string url)
    {
        var controller = BuildController(CreateDbContext());

        var result = await controller.ProxyImage(url);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("http://example.com/image.jpg")]
    [InlineData("https://cdn.example.com/photo.png")]
    [InlineData("https://mymaps.usercontent.google.com/img.jpg")]
    public void IsUrlAllowed_ReturnsTrue_ForValidExternalUrls(string url)
    {
        Assert.True(TripViewerController.IsUrlAllowed(url));
    }

    [Fact]
    public async Task ProxyImage_ServesCachedImage_OnCacheHit()
    {
        var cachedBytes = new byte[] { 0xFF, 0xD8, 0xFF };
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((cachedBytes, "image/jpeg"));

        var controller = BuildController(CreateDbContext(), imageCacheService: cacheMock.Object);

        var result = await controller.ProxyImage("http://example.com/img.jpg");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(cachedBytes, file.FileContents);
        Assert.Equal("image/jpeg", file.ContentType);
    }

    [Fact]
    public async Task ProxyImage_CallsSetAsync_OnCacheMiss()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") }
            }
        });
        var cacheMock = new Mock<IProxiedImageCacheService>();
        cacheMock.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ReturnsAsync(((byte[], string)?)null);

        var controller = BuildController(CreateDbContext(), handler: handler, imageCacheService: cacheMock.Object);

        await controller.ProxyImage("http://example.com/photo.png", optimize: false);

        cacheMock.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Once);
    }

    private TripViewerController BuildController(
        ApplicationDbContext db,
        HttpMessageHandler? handler = null,
        ITripThumbnailService? thumbnailService = null,
        ITripTagService? tagService = null,
        IProxiedImageCacheService? imageCacheService = null)
    {
        var client = new HttpClient(handler ?? new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2 }) }));
        thumbnailService ??= Mock.Of<ITripThumbnailService>();
        tagService ??= Mock.Of<ITripTagService>();
        imageCacheService ??= Mock.Of<IProxiedImageCacheService>();
        var controller = new TripViewerController(
            NullLogger<TripViewerController>.Instance,
            db,
            client,
            thumbnailService,
            tagService,
            imageCacheService);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FakeHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
