using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/[controller]")]
[ApiController]
/// <summary>Authenticated shared Activity catalog for existing cookie and opaque-token clients.</summary>
public class ActivityController : BaseApiController
{
    /// <summary>Uses the existing request-owned authentication authority.</summary>
    public ActivityController(ApplicationDbContext dbContext, ILogger<BaseApiController> logger)
        : base(dbContext, logger)
    {
    }

    /// <summary>
    /// Returns all available activity types for mobile selection.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ActivityTypeDto>), 200)]
    public IActionResult GetActivityTypes()
    {
        if (GetUserFromTokenOrCookie() == null)
            return Unauthorized("Invalid or missing API token.");

        var activities = _dbContext.ActivityTypes
            .OrderBy(a => a.Name)
            .Select(a => new ActivityTypeDto
            {
                Id = a.Id,
                Name = a.Name,
                Description = a.Description
            })
            .ToList();

        return Ok(activities);
    }
}