using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Wayfarer.Models;

namespace Wayfarer.Controllers;

/// <summary>
/// Handles error pages for various HTTP status codes
/// </summary>
[Route("Error")]
public class ErrorController : Controller
{
    private readonly ILogger<ErrorController> _logger;

    public ErrorController(ILogger<ErrorController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generic error handler that accepts any status code
    /// Route: /Error/{statusCode}
    /// </summary>
    [Route("{statusCode:int}")]
    public IActionResult Index(int statusCode)
    {
        // Get the original path that caused the error from the status code re-execute feature
        var statusCodeReExecuteFeature = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        var originalPath = statusCodeReExecuteFeature?.OriginalPath ?? HttpContext.Request.Path.Value;

        _logger.LogWarning("Error page requested for status code: {StatusCode}, Original Path: {Path}",
            statusCode, originalPath);

        Response.StatusCode = statusCode;

        // API routes should return JSON errors (they have their own middleware, but this is a safety net)
        if (originalPath?.StartsWith("/api") == true)
        {
            return new JsonResult(new
            {
                status = statusCode,
                error = statusCode == 404 ? "Not Found" : "Error",
                message = statusCode == 404 ? "The requested API endpoint does not exist." : "An error occurred."
            })
            {
                StatusCode = statusCode
            };
        }

        // Use specific view for 404, generic Error view for others
        if (statusCode == 404)
        {
            ViewData["Title"] = "Page Not Found";
            return View("~/Views/Shared/404.cshtml");
        }

        // For all other errors (500, 403, etc.), use the generic Error view
        ViewData["Title"] = $"Error {statusCode}";
        return View("~/Views/Shared/Error.cshtml", new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }

    /// <summary>
    /// Legacy 404 handler (kept for backward compatibility)
    /// Route: /Error/404
    /// </summary>
    [Route("404")]
    public IActionResult PageNotFound()
    {
        Response.StatusCode = 404;
        ViewData["Title"] = "Page Not Found";
        return View("~/Views/Shared/404.cshtml");
    }
}