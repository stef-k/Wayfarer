using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// Exposes the compiled Wayfarer application version.
/// </summary>
[ApiController]
[Area("Api")]
[Route("api/version")]
public sealed class VersionController : ControllerBase
{
    private readonly IAppVersionProvider _appVersionProvider;

    /// <summary>
    /// Creates the version API endpoint.
    /// </summary>
    /// <param name="appVersionProvider">The provider for the compiled application version.</param>
    public VersionController(IAppVersionProvider appVersionProvider)
    {
        _appVersionProvider = appVersionProvider;
    }

    /// <summary>
    /// Gets the compiled Wayfarer application version.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get()
    {
        return Ok(new { version = _appVersionProvider.Version });
    }
}
