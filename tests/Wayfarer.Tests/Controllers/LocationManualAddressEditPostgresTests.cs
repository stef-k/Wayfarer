using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

/// <summary>Proves stale manual forms cannot overwrite a provider publication that commits first.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationManualAddressEditPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact(Timeout = 20_000)]
    public async Task ProviderPublicationCommittingBeforeEditLockWins()
    {
        var user = await fixture.CreateUserAsync();
        var originalAt = new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero);
        int locationId;
        await using (var seed = fixture.CreateContext())
        {
            var location = new Location
            {
                UserId = user.Id, Coordinates = new Point(25, 40) { SRID = 4326 },
                Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC",
                Address = "Old provider line", Place = "Old city", Region = "Evros", Country = "Greece",
                ReverseGeocodingProvider = "geoapify", ReverseGeocodingStorageMode = "persistent",
                ReverseGeocodedAt = originalAt
            };
            seed.Add(location);
            await seed.SaveChangesAsync();
            locationId = location.Id;
        }
        var model = new AddLocationViewModel
        {
            Id = locationId, Latitude = 40, Longitude = 25, LocalTimestamp = DateTime.UtcNow,
            Address = "Manual overwrite", Place = "Old city", Region = "Evros", Country = "Greece",
            OriginalReverseGeocodingProvider = "geoapify",
            OriginalReverseGeocodingStorageMode = "persistent", OriginalReverseGeocodedAt = originalAt
        };
        await using var provider = fixture.CreateContext();
        await using var providerTransaction = await provider.Database.BeginTransactionAsync();
        var published = await provider.Locations.SingleAsync(item => item.Id == locationId);
        var publishedAt = new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero);
        published.Address = "New provider line";
        published.Place = "New provider city";
        published.ReverseGeocodedAt = publishedAt;
        await provider.SaveChangesAsync();
        var gate = new LocationLockAttemptGate();
        await using var editDb = fixture.CreateContext(gate);
        var edit = Controller(editDb, user).Edit(model, null);
        await gate.Attempted.WaitAsync(TimeSpan.FromSeconds(10));

        await providerTransaction.CommitAsync();
        var result = await edit.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsType<RedirectToActionResult>(result);
        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.SingleAsync(item => item.Id == locationId);
        Assert.Equal("New provider line", saved.Address);
        Assert.Equal("New provider city", saved.Place);
        Assert.Equal(publishedAt, saved.ReverseGeocodedAt);
    }

    private static Wayfarer.Areas.User.Controllers.LocationController Controller(
        ApplicationDbContext db, ApplicationUser user)
    {
        var controller = new Wayfarer.Areas.User.Controllers.LocationController(
            NullLogger<BaseController>.Instance, db,
            new ReverseGeocodingService(new HttpClient(new NoContactHandler()),
                NullLogger<BaseApiController>.Instance), Mock.Of<SseService>(), Mock.Of<IPlaceVisitDetectionService>());
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

    private sealed class LocationLockAttemptGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Attempted => attempted.Task;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Locations\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                attempted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoContactHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Provider contact is forbidden.");
    }
}
