using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    
    // GET: /Public/Trips/View/{id}?embed=true
    [HttpGet]
    [Route("/Public/Trips/{id}")]
    public async Task<IActionResult> View(Guid id, bool embed = false)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var trip = await _dbContext.Trips
            .Include(t => t.Regions!).ThenInclude(r => r.Places!)
            .Include(t => t.Regions!).ThenInclude(a => a.Areas)
            .Include(t => t.Segments!)
            .FirstOrDefaultAsync(t => t.Id == id);

        var owner = trip.UserId == userId;
        
        if (trip == null || !trip.IsPublic)
        {
            return NotFound();
        }
        
        /* ---- layout flags ---- */
        ViewData["LoadLeaflet"] = true;      // needs map
        ViewData["LoadQuill"]   = false;     // no editor
        ViewData["BodyClass"]   = "container-fluid";  // full-width

        ViewBag.IsOwner = owner;
        ViewBag.IsEmbed = embed;             // not an iframe here

        return View("~/Views/Trip/Viewer.cshtml", trip);
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