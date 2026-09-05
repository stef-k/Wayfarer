using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
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

namespace Wayfarer.Tests.Services;

/// <summary>Proves manual editing rejects a stale form after #559 publishes a repair.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    [PostgresFact(Timeout = 30_000)]
    public async Task CompletedIncompleteRepairCannotBeOverwrittenByStaleManualForm()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedIncompleteRepairAsync(user.Id, protection);
        AddLocationViewModel staleForm;
        await using (var before = fixture.CreateContext())
        {
            var location = await before.Locations.SingleAsync(item => item.UserId == user.Id);
            staleForm = new AddLocationViewModel
            {
                Id = location.Id, Latitude = location.Coordinates.Y, Longitude = location.Coordinates.X,
                LocalTimestamp = location.LocalTimestamp, Address = "Manual overwrite",
                Place = location.Place, Region = location.Region, Country = location.Country,
                OriginalReverseGeocodingProvider = location.ReverseGeocodingProvider,
                OriginalReverseGeocodingStorageMode = location.ReverseGeocodingStorageMode,
                OriginalReverseGeocodedAt = location.ReverseGeocodedAt
            };
        }

        var handler = new CoordinatedHandler(user.Id, null);
        var run = Service(protection, handler).RunAsync(user.Id);
        await handler.FirstUserRequestEntered.WaitAsync(TimeSpan.FromSeconds(10));
        handler.Release();
        Assert.Equal(1, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);

        await using var editDb = fixture.CreateContext();
        var result = await Controller(editDb, user).Edit(staleForm, null);

        Assert.IsType<RedirectToActionResult>(result);
        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.SingleAsync(item => item.Id == staleForm.Id);
        Assert.Equal("Keep this address", saved.Address);
        Assert.Equal("Alexandroupolis", saved.Place);
        Assert.NotEqual(staleForm.OriginalReverseGeocodedAt, saved.ReverseGeocodedAt);
    }

    /// <summary>A provider-enriched manual edit commits normalized values visible from a fresh context.</summary>
    [PostgresFact(Timeout = 30_000)]
    public async Task ManualAddressCorrectionPersistsThroughFreshContext()
    {
        var user = await fixture.CreateUserAsync();
        await SeedIncompleteRepairAsync(user.Id, new EphemeralDataProtectionProvider());
        int id;
        await using (var editDb = fixture.CreateContext())
        {
            var location = await editDb.Locations.SingleAsync(item => item.UserId == user.Id);
            id = location.Id;
            location.ReverseGeocodedAt = new DateTimeOffset(2026, 9, 4, 18, 12, 34, TimeSpan.Zero)
                .AddTicks(1234560);
            await editDb.SaveChangesAsync();
            var model = new AddLocationViewModel
            {
                Id = id, Latitude = location.Coordinates.Y, Longitude = location.Coordinates.X,
                LocalTimestamp = location.LocalTimestamp, Address = "  Corrected line  ",
                Place = location.Place, Region = location.Region, Country = location.Country,
                OriginalReverseGeocodingProvider = location.ReverseGeocodingProvider,
                OriginalReverseGeocodingStorageMode = location.ReverseGeocodingStorageMode,
                OriginalReverseGeocodedAt = location.ReverseGeocodedAt
            };
            Assert.IsType<RedirectToActionResult>(await Controller(editDb, user).Edit(model, null));
        }
        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.SingleAsync(item => item.Id == id);
        Assert.Equal("Corrected line", saved.Address);
        Assert.Null(saved.ReverseGeocodingProvider);
        Assert.Null(saved.ReverseGeocodingStorageMode);
        Assert.Null(saved.ReverseGeocodedAt);
        Assert.Null(saved.ResolvedFeatureName);
        Assert.Null(saved.ResolvedFeatureType);
    }

    private static Wayfarer.Areas.User.Controllers.LocationController Controller(
        ApplicationDbContext db, ApplicationUser user)
    {
        var controller = new Wayfarer.Areas.User.Controllers.LocationController(
            NullLogger<BaseController>.Instance, db,
            new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
            Mock.Of<SseService>(), Mock.Of<IPlaceVisitDetectionService>());
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id)], "TestAuth")) };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new TempDataDictionary(context, Mock.Of<ITempDataProvider>());
        var url = new Mock<IUrlHelper>();
        url.Setup(item => item.IsLocalUrl(It.IsAny<string>())).Returns(true);
        url.Setup(item => item.Action(It.IsAny<UrlActionContext>())).Returns("/User/Location");
        controller.Url = url.Object;
        return controller;
    }
}
