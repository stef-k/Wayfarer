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
            .ReturnsAsync((expectedLocations, 2));

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

        var data = okResult.Value.GetType().GetProperty("data")?.GetValue(okResult.Value);
        Assert.NotNull(data);
    }

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
            .ReturnsAsync((new List<CandidateLocationDto>(), 0));

        var (controller, _, _) = BuildController("u1", mockService.Object);

        var result = await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

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
            .ReturnsAsync((new List<CandidateLocationDto>(), 0));

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
            .ReturnsAsync((new List<CandidateLocationDto>(), 0));

        var (controller, _, _) = BuildController("u1", mockService.Object);

        await controller.GetCandidateLocations(
            placeId: Guid.NewGuid(),
            lat: 40.7128,
            lon: -74.0060,
            firstSeenUtc: DateTime.UtcNow.AddHours(-1),
            lastSeenUtc: DateTime.UtcNow,
            page: 3,
            pageSize: 25);

        Assert.Equal(3, capturedPage);
        Assert.Equal(25, capturedPageSize);
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
