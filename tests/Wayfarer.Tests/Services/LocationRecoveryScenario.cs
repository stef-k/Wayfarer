using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Mocks;
using Xunit;

namespace Wayfarer.Tests.Services;

internal sealed class LocationRecoveryScenario : IAsyncDisposable
{
    private readonly ApplicationDbContext db;
    private readonly string userId;
    private readonly List<string> files = [];
    private LocationRecoveryScenario(ApplicationDbContext db, string userId) { this.db = db; this.userId = userId; }

    public static async Task<LocationRecoveryScenario> CreateAsync(ApplicationDbContext db, string userId)
    {
        var user = db.Users.SingleOrDefault(x => x.Id == userId) ?? new ApplicationUser { Id = userId, UserName = userId, DisplayName = userId, IsActive = true };
        if (db.Entry(user).State == Microsoft.EntityFrameworkCore.EntityState.Detached) db.Users.Add(user);
        if (!db.ApiTokens.Any(x => x.UserId == userId)) db.ApiTokens.Add(new ApiToken { UserId = userId, User = user, Token = $"token-{userId}", Name = "recovery test" });
        if (!db.ApplicationSettings.Any()) db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        await db.SaveChangesAsync();
        return new(db, userId);
    }

    public async Task<int> ImportAsync(Guid key) => (await ImportWithResultAsync(key)).LocationId;
    public async Task<(int LocationId, int SkippedDuplicates)> ImportWithResultAsync(Guid key)
    {
        var path = Path.GetTempFileName(); files.Add(path);
        await File.WriteAllTextAsync(path, $"Latitude,Longitude,TimestampUtc,IdempotencyKey\r\n37.1,22.2,2026-08-22T10:00:00Z,{key:D}");
        var import = new LocationImport { UserId = userId, FileType = LocationImportFileType.Csv, FilePath = path, Status = ImportStatus.InProgress, TotalRecords = 0, LastProcessedIndex = 0 };
        db.LocationImports.Add(import); await db.SaveChangesAsync();
        await CreateImportService().ProcessImport(import.Id, CancellationToken.None);
        var row = db.Locations.Single(x => x.UserId == userId && x.IdempotencyKey == key);
        return (row.Id, db.LocationImports.Single(x => x.Id == import.Id).SkippedDuplicates);
    }

    public async Task<int> DrainAsync(Guid key)
    {
        var controller = CreateController();
        controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] = key.ToString("D");
        var result = Assert.IsType<OkObjectResult>(await controller.LogLocation(new GpsLoggerLocationDto
            { Latitude = 37.1, Longitude = 22.2, Timestamp = new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc) }));
        return (int)result.Value!.GetType().GetProperty("locationId")!.GetValue(result.Value)!;
    }

    public int LocationCount(Guid key) => db.Locations.Count(x => x.UserId == userId && x.IdempotencyKey == key);

    private LocationImportService CreateImportService()
    {
        var factory = NullLoggerFactory.Instance;
        return new LocationImportService(db, new ReverseGeocodingService(new HttpClient(new FakeHandler()), NullLogger<BaseApiController>.Instance),
            NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(factory), new SseService());
    }

    private LocationController CreateController()
    {
        var cache = new MemoryCache(new MemoryCacheOptions()); var locationService = new LocationService(db);
        var controller = new LocationController(db, NullLogger<BaseApiController>.Instance, cache,
            new ApplicationSettingsService(db, cache), new ReverseGeocodingService(new HttpClient(new FakeHandler()), NullLogger<BaseApiController>.Instance),
            locationService, new SseService(), new LocationStatsService(db), locationService, new NullPlaceVisitDetectionService());
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")) };
        context.Request.Headers["Authorization"] = $"Bearer token-{userId}";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    public ValueTask DisposeAsync() { foreach (var path in files) File.Delete(path); return ValueTask.CompletedTask; }
    private sealed class FakeHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"features\":[]}") }); }
}
