using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Tests for the BackfillController API endpoints.
/// </summary>
public class BackfillControllerTests : TestBase
{
    /// <summary>Same-type infrastructure failures retain the status without exposing internal text.</summary>
    [Theory]
    [InlineData("Trip not found or access denied.", "Trip not found or access denied.")]
    [InlineData("private-backfill-663", "Unable to retrieve backfill information.")]
    public async Task GetInfo_OnlyPublishesTheKnownNotFoundRule(string failure, string expected)
    {
        var service = new Mock<IVisitBackfillService>();
        service.Setup(s => s.GetInfoAsync("u1", It.IsAny<Guid>(), null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(failure));
        var (controller, _, _) = BuildController("u1", service.Object);
        var result = Assert.IsType<NotFoundObjectResult>(await controller.Info(Guid.NewGuid()));
        Assert.Equal(expected, result.Value?.GetType().GetProperty("message")?.GetValue(result.Value));
    }

    #region GetCandidateLocations Tests

    [Fact]
    public async Task GetCandidateLocations_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        var (controller, _, _) = BuildController(null);

        var result = await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GetCandidateLocations_ReturnsBadRequest_WhenPlaceIdEmpty()
    {
        var (controller, _, _) = BuildController("u1");

        var result = await controller.GetCandidateLocations(
            placeId: Guid.Empty,
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("placeId", badRequest.Value?.ToString());
    }

    [Theory]
    [InlineData(-91, 0)]  // Latitude too low
    [InlineData(91, 0)]   // Latitude too high
    [InlineData(0, -181)] // Longitude too low
    [InlineData(0, 181)]  // Longitude too high
    public async Task GetCandidateLocations_ReturnsBadRequest_WhenCoordinatesInvalid(double lat, double lon)
    {
        var (controller, _, _) = BuildController("u1");

        var result = await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: lat,
            lon: lon,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetCandidateLocations_ReturnsBadRequest_WhenTimestampsMissing()
    {
        var (controller, _, _) = BuildController("u1");

        var result = await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: default,
            lastSeenUtc: default);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("firstSeenUtc", badRequest.Value?.ToString());
    }

    /// <summary>Default requests retain the response envelope, locations, count and 1/50 pagination.</summary>
    [Fact]
    public async Task GetCandidateLocations_ReturnsOk_WithValidParameters()
    {
        var mockService = new Mock<IVisitBackfillService>();
        var expectedLocations = new List<CandidateLocationDto>
        {
            new()
            {
                Id = 1,
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-30),
                Latitude = 40.7128,
                Longitude = -74.0060,
                Accuracy = 10,
                DistanceMeters = 25.5
            },
            new()
            {
                Id = 2,
                LocalTimestamp = DateTime.UtcNow.AddMinutes(-15),
                Latitude = 40.7130,
                Longitude = -74.0062,
                Accuracy = 15,
                DistanceMeters = 30.2
            }
        };

        mockService
            .Setup(s => s.GetCandidateLocationsAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((expectedLocations, 2, 1, 50));

        var (controller, _, _) = BuildController("u1", mockService.Object);

        var result = await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        var success = okResult.Value.GetType().GetProperty("success")?.GetValue(okResult.Value);
        Assert.Equal(true, success);

        var data = Assert.IsType<CandidateLocationsResponseDto>(
            okResult.Value.GetType().GetProperty("data")?.GetValue(okResult.Value));
        Assert.Same(expectedLocations, data.Locations);
        Assert.Equal(2, data.TotalCount);
        Assert.Equal(1, data.Page);
        Assert.Equal(50, data.PageSize);
    }

    /// <summary>Empty results still report the effective query limit rather than the returned row count.</summary>
    [Fact]
    public async Task GetCandidateLocations_ReturnsOk_WithEmptyLocations()
    {
        var mockService = new Mock<IVisitBackfillService>();
        mockService
            .Setup(s => s.GetCandidateLocationsAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<CandidateLocationDto>(), 0, 1, 50));

        var (controller, _, _) = BuildController("u1", mockService.Object);

        var result = await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var data = Assert.IsType<CandidateLocationsResponseDto>(
            okResult.Value.GetType().GetProperty("data")?.GetValue(okResult.Value));
        Assert.Empty(data.Locations);
        Assert.Equal(0, data.TotalCount);
        Assert.Equal(1, data.Page);
        Assert.Equal(50, data.PageSize);
    }

    /// <summary>Service metadata owns response pagination; raw inputs and cancellation reach it unchanged.</summary>
    [Fact]
    public async Task GetCandidateLocations_ReportsEffectivePagination_AndForwardsCancellation()
    {
        var service = new Mock<IVisitBackfillService>(MockBehavior.Strict);
        var placeId = Guid.NewGuid();
        var firstSeenUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var lastSeenUtc = firstSeenUtc.AddMinutes(30);
        using var cancellation = new CancellationTokenSource();
        var locations = new List<CandidateLocationDto> { new() { Id = 17 } };
        service.Setup(s => s.GetCandidateLocationsAsync("u1", placeId, 37, 23,
                firstSeenUtc, lastSeenUtc, 500, 0, 1000, cancellation.Token))
            .ReturnsAsync((locations, 3001, 1, 200));
        var (controller, _, _) = BuildController("u1", service.Object);

        var result = await controller.GetCandidateLocations(placeId, 37, 23,
            firstSeenUtc, lastSeenUtc, radius: 500, page: 0, pageSize: 1000,
            cancellationToken: cancellation.Token);

        service.Verify(s => s.GetCandidateLocationsAsync("u1", placeId, 37, 23,
            firstSeenUtc, lastSeenUtc, 500, 0, 1000, cancellation.Token), Times.Once);
        service.VerifyNoOtherCalls();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(true, ok.Value!.GetType().GetProperty("success")?.GetValue(ok.Value));
        var data = Assert.IsType<CandidateLocationsResponseDto>(
            ok.Value.GetType().GetProperty("data")?.GetValue(ok.Value));
        Assert.Same(locations, data.Locations);
        Assert.Equal(3001, data.TotalCount);
        Assert.Equal(1, data.Page);
        Assert.Equal(200, data.PageSize);
    }

    /// <summary>A custom search radius reaches the service without changing the default pagination.</summary>
    [Fact]
    public async Task GetCandidateLocations_UsesCustomRadius_WhenProvided()
    {
        var mockService = new Mock<IVisitBackfillService>();
        var capturedRadius = 0;

        mockService
            .Setup(s => s.GetCandidateLocationsAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Guid, double, double, DateTime, DateTime, int, int, int, CancellationToken>(
                (_, _, _, _, _, _, radius, _, _, _) => capturedRadius = radius)
            .ReturnsAsync((new List<CandidateLocationDto>(), 0, 1, 50));

        var (controller, _, _) = BuildController("u1", mockService.Object);

        await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow,
            radius: 500);

        Assert.Equal(500, capturedRadius);
    }

    /// <summary>Explicit pagination is forwarded and remains present even when that page is empty.</summary>
    [Fact]
    public async Task GetCandidateLocations_UsesPagination_WhenProvided()
    {
        var mockService = new Mock<IVisitBackfillService>();
        var capturedPage = 0;
        var capturedPageSize = 0;

        mockService
            .Setup(s => s.GetCandidateLocationsAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Guid, double, double, DateTime, DateTime, int, int, int, CancellationToken>(
                (_, _, _, _, _, _, _, page, pageSize, _) =>
                {
                    capturedPage = page;
                    capturedPageSize = pageSize;
                })
            .ReturnsAsync((new List<CandidateLocationDto>(), 0, 3, 25));

        var (controller, _, _) = BuildController("u1", mockService.Object);

        var result = await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow,
            page: 3,
            pageSize: 25);

        Assert.Equal(3, capturedPage);
        Assert.Equal(25, capturedPageSize);
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<CandidateLocationsResponseDto>(
            ok.Value!.GetType().GetProperty("data")?.GetValue(ok.Value));
        Assert.Empty(data.Locations);
        Assert.Equal(0, data.TotalCount);
        Assert.Equal(3, data.Page);
        Assert.Equal(25, data.PageSize);
    }

    [Fact]
    public async Task GetCandidateLocations_Returns500_WhenServiceThrows()
    {
        var mockService = new Mock<IVisitBackfillService>();
        mockService
            .Setup(s => s.GetCandidateLocationsAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database connection failed"));

        var (controller, _, _) = BuildController("u1", mockService.Object);

        var result = await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow);

        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, statusCodeResult.StatusCode);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Builds a BackfillController with optional user authentication and mocked service.
    /// </summary>
    private (BackfillController Controller, ApplicationDbContext Db, Mock<IVisitBackfillService> ServiceMock)
        BuildController(string? userId, IVisitBackfillService? service = null)
    {
        var db = CreateDbContext();

        // Add default settings
        db.ApplicationSettings.Add(new ApplicationSettings
        {
            Id = 1,
            VisitedMaxSearchRadiusMeters = 150,
            VisitedSuggestionMaxRadiusMultiplier = 50
        });
        db.SaveChanges();

        var mockService = new Mock<IVisitBackfillService>();
        var controller = new BackfillController(
            db,
            NullLogger<BaseApiController>.Instance,
            service ?? mockService.Object);

        if (userId != null)
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = BuildHttpContextWithUser(userId)
            };
        }
        else
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        return (controller, db, mockService);
    }

    #endregion
}
