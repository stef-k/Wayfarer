using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.User.Controllers;

[Area("User")]
[Authorize(Roles = "User")]
[Route("User/Trip/{tripId:guid}/Tags")]
[AutoValidateAntiforgeryToken]
public sealed partial class TripTagsController : BaseController
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

    /// <summary>Refuses the legacy tag attach invocation after cutover.</summary>
    [HttpPost]
    public Task<IActionResult> Attach(Guid tripId, [FromBody] ModifyTagsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(TripMetadataReceiptRoutes.UpgradeRequired().ToActionResult());

    /// <summary>Refuses the legacy tag detach invocation after cutover.</summary>
    [HttpDelete("{slug}")]
    public Task<IActionResult> Remove(Guid tripId, string slug, CancellationToken cancellationToken) =>
        Task.FromResult(TripMetadataReceiptRoutes.UpgradeRequired().ToActionResult());

    public sealed class ModifyTagsRequest
    {
        public List<string> Tags { get; set; } = new();
    }
}
