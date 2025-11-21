using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// API settings exposure.
/// </summary>
public class ApiSettingsControllerTests : TestBase
{
    [Fact]
    public void GetSettings_ReturnsDto_WithDefaults()
    {
        var db = CreateDbContext();
        var settingsSvc = new Mock<IApplicationSettingsService>();
        settingsSvc.Setup(s => s.GetSettings()).Returns(new ApplicationSettings
        {
            LocationTimeThresholdMinutes = 7,
            LocationDistanceThresholdMeters = 25
        });
        var controller = new SettingsController(db, NullLogger<BaseApiController>.Instance, settingsSvc.Object);

        var result = controller.GetSettings();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<ApiSettingsDto>(ok.Value);
        Assert.Equal(7, dto.LocationTimeThresholdMinutes);
        Assert.Equal(25, dto.LocationDistanceThresholdMeters);
    }
}
