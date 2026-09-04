using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Point = NetTopologySuite.Geometries.Point;

namespace Wayfarer.Tests.Controllers;

/// <summary>Proves manual Location address editing and provenance behavior.</summary>
public sealed class LocationManualAddressEditTests : TestBase
{
    [Fact]
    public async Task AddressChangeTrimsClearsProvenanceAndMakesNoProviderContact()
    {
        var scenario = await CreateScenarioAsync("address-owner");
        var handler = new CountingHandler();
        var model = EditModel(scenario.Location);
        model.Address = "  New line  ";
        model.Place = " ";

        await Controller(scenario, handler).Edit(model, null);

        var saved = await scenario.Db.Locations.SingleAsync(item => item.Id == scenario.Location.Id);
        Assert.Equal("New line", saved.Address);
        Assert.Null(saved.Place);
        Assert.Null(saved.ReverseGeocodingProvider);
        Assert.Null(saved.ReverseGeocodingStorageMode);
        Assert.Null(saved.ReverseGeocodedAt);
        Assert.Null(saved.ResolvedFeatureName);
        Assert.Null(saved.ResolvedFeatureType);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CoordinateChangeRetainsOnlyExplicitlyChangedAddressFields()
    {
        var scenario = await CreateScenarioAsync("coordinate-owner");
        scenario.Location.FullAddress = "Old full address";
        await scenario.Db.SaveChangesAsync();
        var model = EditModel(scenario.Location);
        model.Latitude = 40.8;
        model.Longitude = 25.8;
        model.Address = "New manual line";

        await Controller(scenario).Edit(model, null);

        var saved = await scenario.Db.Locations.SingleAsync(item => item.Id == scenario.Location.Id);
        Assert.Equal("New manual line", saved.Address);
        Assert.Null(saved.FullAddress);
        Assert.Null(saved.Place);
        Assert.Null(saved.Region);
        Assert.Null(saved.Country);
        Assert.Null(saved.ReverseGeocodingProvider);
    }

    [Fact]
    public async Task UnchangedAddressPreservesProviderProvenance()
    {
        var scenario = await CreateScenarioAsync("unchanged-owner");

        await Controller(scenario).Edit(EditModel(scenario.Location), null);

        var saved = await scenario.Db.Locations.SingleAsync(item => item.Id == scenario.Location.Id);
        Assert.Equal("geoapify", saved.ReverseGeocodingProvider);
        Assert.Equal("persistent", saved.ReverseGeocodingStorageMode);
        Assert.Equal(scenario.Location.ReverseGeocodedAt, saved.ReverseGeocodedAt);
    }

    [Fact]
    public async Task StaleProviderPublicationIsNotOverwritten()
    {
        var scenario = await CreateScenarioAsync("stale-owner");
        var staleForm = EditModel(scenario.Location);
        staleForm.Address = "Manual overwrite";
        var newerAt = DateTimeOffset.UtcNow;
        scenario.Location.Address = "New provider line";
        scenario.Location.Place = "New provider city";
        scenario.Location.ReverseGeocodedAt = newerAt;
        await scenario.Db.SaveChangesAsync();

        var result = await Controller(scenario).Edit(staleForm, null);

        Assert.IsType<RedirectToActionResult>(result);
        var saved = await scenario.Db.Locations.SingleAsync(item => item.Id == scenario.Location.Id);
        Assert.Equal("New provider line", saved.Address);
        Assert.Equal("New provider city", saved.Place);
        Assert.Equal(newerAt, saved.ReverseGeocodedAt);
    }

    [Fact]
    public void AddressFieldsEnforceFiveHundredCharacterLimit()
    {
        var model = new AddLocationViewModel { Address = new string('x', 501) };
        var results = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), results, true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(AddLocationViewModel.Address)));
    }

    private async Task<Scenario> CreateScenarioAsync(string userId)
    {
        var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: userId, username: userId);
        var location = new Location
        {
            UserId = user.Id, Coordinates = new Point(0, 0) { SRID = 4326 },
            Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC",
            Address = "Old line", Place = "Old city", Region = "Evros", Country = "Greece",
            ReverseGeocodingProvider = "geoapify", ReverseGeocodingStorageMode = "persistent",
            ReverseGeocodedAt = DateTimeOffset.UtcNow.AddHours(-1), ResolvedFeatureName = "Old feature",
            ResolvedFeatureType = "amenity"
        };
        db.AddRange(user, location);
        await db.SaveChangesAsync();
        return new(db, user, location);
    }

    private static AddLocationViewModel EditModel(Location location) => new()
    {
        Id = location.Id, Latitude = location.Coordinates.Y, Longitude = location.Coordinates.X,
        LocalTimestamp = location.LocalTimestamp, Address = location.Address, FullAddress = location.FullAddress,
        AddressNumber = location.AddressNumber, StreetName = location.StreetName, PostCode = location.PostCode,
        Place = location.Place, Region = location.Region, Country = location.Country,
        OriginalReverseGeocodingProvider = location.ReverseGeocodingProvider,
        OriginalReverseGeocodingStorageMode = location.ReverseGeocodingStorageMode,
        OriginalReverseGeocodedAt = location.ReverseGeocodedAt
    };

    private static Wayfarer.Areas.User.Controllers.LocationController Controller(
        Scenario scenario, HttpMessageHandler? handler = null)
    {
        var controller = new Wayfarer.Areas.User.Controllers.LocationController(
            NullLogger<BaseController>.Instance, scenario.Db,
            new ReverseGeocodingService(new HttpClient(handler ?? new CountingHandler()),
                NullLogger<BaseApiController>.Instance), Mock.Of<SseService>(), Mock.Of<IPlaceVisitDetectionService>());
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, scenario.User.Id)], "TestAuth")) };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());
        var url = new Mock<IUrlHelper>();
        url.Setup(item => item.IsLocalUrl(It.IsAny<string>())).Returns(true);
        url.Setup(item => item.Action(It.IsAny<UrlActionContext>())).Returns("/User/Location");
        controller.Url = url.Object;
        return controller;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed record Scenario(ApplicationDbContext Db, ApplicationUser User, Location Location);
}
