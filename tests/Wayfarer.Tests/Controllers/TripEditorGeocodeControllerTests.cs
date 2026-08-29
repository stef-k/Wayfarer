using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused tests for the Trip Editor geocode proxy controller contract.
/// </summary>
public sealed class TripEditorGeocodeControllerTests : TestBase
{
    [Fact]
    public void SearchGeocodeUsesAntiforgeryProtectedPost()
    {
        var method = typeof(TripEditorController).GetMethod(nameof(TripEditorController.SearchGeocode));

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).SingleOrDefault());
        Assert.NotNull(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true).SingleOrDefault());
        var sizeLimit = Assert.Single(method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: true));
        Assert.Equal(1024, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)sizeLimit).MaxRequestBodySize);
        Assert.Empty(method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true));
    }

    [Fact]
    public async Task SearchGeocodeRequiresAuthenticatedEditorUser()
    {
        using var db = CreateDbContext();
        var controller = BuildController(db, new FakeGeocodeSearchService());

        SetSearchBody(controller, "athens", null);
        var result = await controller.SearchGeocode(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task SearchGeocodeReturnsForbiddenForNonOwner()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db, new FakeGeocodeSearchService());
        ConfigureControllerWithUserRole(controller, "other-user");

        SetSearchBody(controller, "athens", null);
        var result = await controller.SearchGeocode(trip.Id, CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchGeocodeReturnsQValidationKeyWhenQueryMissing(string? query)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db, new FakeGeocodeSearchService());
        ConfigureControllerWithUserRole(controller, "owner-user");

        SetSearchBody(controller, query, null);
        var result = await controller.SearchGeocode(trip.Id, CancellationToken.None);

        Assert.Contains("q", AssertValidationProblem(result).Errors.Keys);
    }

    [Fact]
    public async Task SearchGeocodeReturnsQValidationKeyWhenQueryTooShort()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db, new FakeGeocodeSearchService());
        ConfigureControllerWithUserRole(controller, "owner-user");

        SetSearchBody(controller, "ab", null);
        var result = await controller.SearchGeocode(trip.Id, CancellationToken.None);

        Assert.Contains("q", AssertValidationProblem(result).Errors.Keys);
    }

    [Fact]
    public async Task SearchGeocodeReturnsLimitValidationKeyWhenLimitBelowMinimum()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var controller = BuildController(db, new FakeGeocodeSearchService());
        ConfigureControllerWithUserRole(controller, "owner-user");

        SetSearchBody(controller, "athens", 0);
        var result = await controller.SearchGeocode(trip.Id, CancellationToken.None);

        Assert.Contains("limit", AssertValidationProblem(result).Errors.Keys);
    }

    [Theory]
    [InlineData(null, 6)]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(7, 6)]
    [InlineData(int.MaxValue, 6)]
    public async Task SearchGeocodeNormalizesLimitBeforeServiceInvocation(int? requestedLimit, int expectedLimit)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var service = new FakeGeocodeSearchService();
        var controller = BuildController(db, service);
        ConfigureControllerWithUserRole(controller, "owner-user");

        SetSearchBody(controller, "athens", requestedLimit);
        var result = await controller.SearchGeocode(trip.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, service.CallCount);
        Assert.Equal(expectedLimit, service.LastLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SearchGeocodeRejectsLimitBelowMinimumBeforeServiceInvocation(int requestedLimit)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var service = new FakeGeocodeSearchService();
        var controller = BuildController(db, service);
        ConfigureControllerWithUserRole(controller, "owner-user");

        SetSearchBody(controller, "athens", requestedLimit);
        var result = await controller.SearchGeocode(trip.Id, CancellationToken.None);

        Assert.Contains("limit", AssertValidationProblem(result).Errors.Keys);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task SearchGeocodeReturnsNoResultsAsSuccessfulResponseWithAttribution()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var service = new FakeGeocodeSearchService
        {
            Outcome = TripEditorGeocodeSearchOutcome.Success(new EditorGeocodeSearchResponseDto("missing", "Data source", Array.Empty<EditorGeocodeSearchResultDto>()))
        };
        var controller = BuildController(db, service);
        ConfigureControllerWithUserRole(controller, "owner-user");

        SetSearchBody(controller, "missing", null);
        var result = await controller.SearchGeocode(trip.Id, CancellationToken.None);

        var response = Assert.IsType<EditorGeocodeSearchResponseDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(response.Results);
        Assert.Equal("Data source", response.Attribution);
    }

    [Theory]
    [InlineData(TripEditorGeocodeSearchStatus.LocalRateLimited, StatusCodes.Status429TooManyRequests)]
    [InlineData(TripEditorGeocodeSearchStatus.ProviderRateLimited, StatusCodes.Status429TooManyRequests)]
    [InlineData(TripEditorGeocodeSearchStatus.ProviderMalformed, StatusCodes.Status502BadGateway)]
    [InlineData(TripEditorGeocodeSearchStatus.ProviderUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(TripEditorGeocodeSearchStatus.ProviderTimeout, StatusCodes.Status504GatewayTimeout)]
    public async Task SearchGeocodeMapsProviderFailures(TripEditorGeocodeSearchStatus serviceStatus, int expectedStatus)
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        var service = new FakeGeocodeSearchService { Outcome = TripEditorGeocodeSearchOutcome.Failure(serviceStatus) };
        var controller = BuildController(db, service);
        ConfigureControllerWithUserRole(controller, "owner-user");

        SetSearchBody(controller, "athens", null);
        var result = await controller.SearchGeocode(trip.Id, CancellationToken.None);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(expectedStatus, status.StatusCode);
    }

    [Fact]
    public async Task SearchGeocodePropagatesCallerCancellation()
    {
        using var db = CreateDbContext();
        var trip = SeedTrip(db, "owner-user");
        using var cancellation = new CancellationTokenSource();
        var service = new CancelingGeocodeSearchService(cancellation);
        var controller = BuildController(db, service);
        ConfigureControllerWithUserRole(controller, "owner-user");

        SetSearchBody(controller, "athens", null);
        await Assert.ThrowsAsync<TaskCanceledException>(() => controller.SearchGeocode(trip.Id, cancellation.Token));
    }

    private static TripEditorController BuildController(ApplicationDbContext db, ITripEditorGeocodeSearchService geocodeSearch)
    {
        var environment = Mock.Of<IWebHostEnvironment>(e => e.WebRootPath == Path.GetTempPath());
        return new TripEditorController(
            db,
            environment,
            Mock.Of<IIconColorProvider>(),
            Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ICacheWarmupScheduler>(),
            Mock.Of<ITripTagService>(),
            new TripEditorRegionMutationService(db),
            new TripEditorPlaceMutationService(db, environment, Mock.Of<IIconColorProvider>(), new ReverseGeocodingService(new HttpClient(), Mock.Of<ILogger<BaseApiController>>())),
            new TripEditorAreaMutationService(db),
            new TripEditorSegmentMutationService(db),
            Mock.Of<ILogger<TripEditorController>>(),
            geocodeSearch);
    }

    private static void ConfigureControllerWithUserRole(ControllerBase controller, string userId, string role = "User")
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = BuildHttpContextWithUser(userId, role)
        };
    }

    private static void SetSearchBody(ControllerBase controller, string? query, int? limit)
    {
        controller.ControllerContext.HttpContext ??= new DefaultHttpContext();
        var json = System.Text.Json.JsonSerializer.Serialize(new { query, limit });
        controller.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static ValidationProblemDetails AssertValidationProblem(IActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("application/problem+json", badRequest.ContentTypes);
        return Assert.IsType<ValidationProblemDetails>(badRequest.Value);
    }

    private static Trip SeedTrip(ApplicationDbContext db, string userId)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip", UpdatedAt = DateTime.UtcNow };
        db.Trips.Add(trip);
        db.SaveChanges();
        return trip;
    }

    private sealed class FakeGeocodeSearchService : ITripEditorGeocodeSearchService
    {
        public int LastLimit { get; private set; }
        public int CallCount { get; private set; }

        public TripEditorGeocodeSearchOutcome Outcome { get; init; } =
            TripEditorGeocodeSearchOutcome.Success(new EditorGeocodeSearchResponseDto("athens", "Data source", new[]
            {
                new EditorGeocodeSearchResultDto("nominatim:1", "nominatim", "Athens", "Athens, Greece", "Greece", "place", "city", 37.9838, 23.7275)
            }));

        public Task<TripEditorGeocodeSearchOutcome> SearchAsync(string userId, string query, int limit, CancellationToken cancellationToken)
        {
            CallCount += 1;
            LastLimit = limit;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class CancelingGeocodeSearchService : ITripEditorGeocodeSearchService
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelingGeocodeSearchService(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public Task<TripEditorGeocodeSearchOutcome> SearchAsync(string userId, string query, int limit, CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            throw new TaskCanceledException("Caller canceled geocode search.");
        }
    }
}
