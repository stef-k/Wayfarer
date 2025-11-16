using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/tags")]
[ApiController]
public sealed class TagsController : BaseApiController
{
    private readonly ITripTagService _tripTagService;

    public TagsController(
        ApplicationDbContext dbContext,
        ILogger<BaseApiController> logger,
        ITripTagService tripTagService)
        : base(dbContext, logger)
    {
        _tripTagService = tripTagService;
    }

    [HttpGet("suggest")]
    public async Task<IActionResult> Suggest([FromQuery] string? q = null, [FromQuery] int take = 10, CancellationToken cancellationToken = default)
    {
        var items = await _tripTagService.GetSuggestionsAsync(q, Math.Clamp(take, 1, 50), cancellationToken);
        return Ok(items);
    }

    [HttpGet("popular")]
    public async Task<IActionResult> Popular([FromQuery] int take = 20, CancellationToken cancellationToken = default)
    {
        var items = await _tripTagService.GetPopularAsync(Math.Clamp(take, 1, 50), cancellationToken);
        return Ok(items);
    }
}
