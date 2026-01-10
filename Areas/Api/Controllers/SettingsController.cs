using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Models;
using Microsoft.Extensions.Logging;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/[controller]")]
[ApiController]
public class SettingsController : BaseApiController
{
    private readonly IApplicationSettingsService _settingsService;

    public SettingsController(ApplicationDbContext dbContext, ILogger<BaseApiController> logger, IApplicationSettingsService settingsService)
        : base(dbContext, logger)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Returns location-related app settings (no auth required).
    /// </summary>
    [HttpGet]
    public IActionResult GetSettings()
    {
        var settings = _settingsService.GetSettings();

        var dto = new ApiSettingsDto
        {
            LocationTimeThresholdMinutes = settings?.LocationTimeThresholdMinutes ?? 5,
            LocationDistanceThresholdMeters = settings?.LocationDistanceThresholdMeters ?? 15,
            LocationAccuracyThresholdMeters = settings?.LocationAccuracyThresholdMeters ?? 100
        };

        return Ok(dto);
    }
}