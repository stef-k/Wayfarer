using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

namespace Wayfarer.Tests.Controllers;

/// <summary>Legacy Web POST is retired; protected manual effects are exercised through routed PostgreSQL tests.</summary>
public sealed class LocationManualAddressEditTests : TestBase
{
    /// <summary>Neither valid nor model-invalid full forms can bypass protected originals or contact a provider.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPostRefusesBeforeAnyWriter(bool invalid)
    {
        using var db = CreateDbContext();
        var handler = new NoContact();
        var controller = new Wayfarer.Areas.User.Controllers.LocationController(NullLogger<BaseController>.Instance, db,
            new ReverseGeocodingService(new HttpClient(handler), NullLogger<BaseApiController>.Instance),
            Mock.Of<SseService>(), Mock.Of<IPlaceVisitDetectionService>())
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        if (invalid) controller.ModelState.AddModelError("Latitude", "Invalid");
        var result = await controller.Edit(new AddLocationViewModel { Id = 421, Latitude = 1, Longitude = 2, Address = "unsafe" }, null);
        Assert.Equal(428, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(db.Locations);
    }

    /// <summary>Existing Web Create input metadata retains its documented address length limit.</summary>
    [Fact]
    public void AddressFieldsEnforceFiveHundredCharacterLimit()
    {
        var model = new AddLocationViewModel { Address = new string('x', 501) };
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), results, true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(AddLocationViewModel.Address)));
    }

    /// <summary>Unexpected contact is a hard test failure.</summary>
    private sealed class NoContact : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Retired form contacted a provider.");
    }
}
