using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Public.Controllers;

[Area("Public")]
[AllowAnonymous]
[Route("Public/Tags")]
public sealed class TagsController : BaseController
{
    private readonly ITripTagService _tripTagService;

    public TagsController(
        ILogger<TagsController> logger,
        ApplicationDbContext dbContext,
        ITripTagService tripTagService)
        : base(logger, dbContext)
    {
        _tripTagService = tripTagService;
    }

    [HttpGet("Suggest")]
    public async Task<IActionResult> Suggest(string? q, int take = 10, CancellationToken cancellationToken = default)
    {
        var items = await _tripTagService.GetSuggestionsAsync(q, Math.Clamp(take, 1, 50), cancellationToken);
        return Json(items);
    }

    [HttpGet("Popular")]
    public async Task<IActionResult> Popular(int take = 20, CancellationToken cancellationToken = default)
    {
        var items = await _tripTagService.GetPopularAsync(Math.Clamp(take, 1, 50), cancellationToken);
        return Json(items);
    }
}
