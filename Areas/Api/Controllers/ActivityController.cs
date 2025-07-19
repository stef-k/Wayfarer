using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/[controller]")]
[ApiController]
public class ActivityController : BaseApiController
{
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