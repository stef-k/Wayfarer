using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Admin settings controller basics.
/// </summary>
public class AdminSettingsControllerTests : TestBase
{
    [Fact]
    public async Task Index_ReturnsView_WithSettings()
    {
        var db = CreateDbContext();
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        db.SaveChanges();
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        var settingsService = new ApplicationSettingsService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var controller = new SettingsController(NullLogger<BaseController>.Instance, db, settingsService, new TileCacheService(), env.Object);

        var result = await controller.Index();

        Assert.IsType<ViewResult>(result);
    }
}
