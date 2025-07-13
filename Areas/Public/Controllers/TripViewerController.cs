using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;

namespace Wayfarer.Areas.Public.Controllers;

public class TripViewerController : BaseController
{
    private HttpClient _httpClient;
    public TripViewerController(ILogger<TripViewerController> logger, ApplicationDbContext dbContext, HttpClient httpClient)
        : base(logger, dbContext)
    {
        _httpClient = httpClient;
    }
    
    [AllowAnonymous]  
    [HttpGet("Public/ProxyImage")]
    public async Task<IActionResult> ProxyImage(string url)
    {
        using var resp = await _httpClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return StatusCode((int)resp.StatusCode);

        var contentType = resp.Content.Headers.ContentType?.MediaType
                          ?? "application/octet-stream";
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return File(bytes, contentType);
    }
}