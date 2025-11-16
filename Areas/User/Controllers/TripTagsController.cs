using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
[Route("User/Trip/{tripId:guid}/Tags")]
[AutoValidateAntiforgeryToken]
public sealed class TripTagsController : BaseController
{
    private readonly ITripTagService _tripTagService;

    public TripTagsController(
        ILogger<TripTagsController> logger,
        ApplicationDbContext dbContext,
        ITripTagService tripTagService)
        : base(logger, dbContext)
    {
        _tripTagService = tripTagService;
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid tripId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        var tags = await _tripTagService.GetTagsForTripAsync(tripId, userId, cancellationToken);
        return Ok(new { tags });
    }

    [HttpPost]
    public async Task<IActionResult> Attach(Guid tripId, [FromBody] ModifyTagsRequest request, CancellationToken cancellationToken)
    {
        if (request?.Tags == null || request.Tags.Count == 0)
        {
            return BadRequest(new { error = "Please provide at least one tag." });
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        try
        {
            var tags = await _tripTagService.AttachTagsAsync(tripId, request.Tags, userId, cancellationToken);
            return Ok(new { tags });
        }
        catch (ValidationException vex)
        {
            return BadRequest(new { error = vex.Message });
        }
        catch (KeyNotFoundException knf)
        {
            return NotFound(new { error = knf.Message });
        }
    }

    [HttpDelete("{slug}")]
    public async Task<IActionResult> Remove(Guid tripId, string slug, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null)
        {
            return Unauthorized();
        }

        try
        {
            var removed = await _tripTagService.DetachTagAsync(tripId, slug, userId, cancellationToken);
            if (!removed)
            {
                return NotFound(new { error = "Tag not found on this trip." });
            }

            var tags = await _tripTagService.GetTagsForTripAsync(tripId, userId, cancellationToken);
            return Ok(new { tags });
        }
        catch (KeyNotFoundException knf)
        {
            return NotFound(new { error = knf.Message });
        }
    }

    public sealed class ModifyTagsRequest
    {
        public List<string> Tags { get; set; } = new();
    }
}
