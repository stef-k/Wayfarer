using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Wayfarer.Models;
namespace Wayfarer.Areas.Api.Controllers;

[Area("Api")]
[Route("api/users")]
[ApiController]
public class UsersController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<UsersController> _logger;

    public UsersController(ApplicationDbContext dbContext, ILogger<UsersController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Deletes all locations for the given user.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    // DELETE /api/users/{userId}/locations
    [HttpDelete("{userId}/locations")]
    [Authorize]
    public async Task<IActionResult> DeleteAllUserLocations([FromRoute] string userId)
    {
        var userExists = await _dbContext.Users.AnyAsync(u => u.Id == userId);
        if (!userExists)
            return NotFound(new { message = "User not found." });

        var callerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (callerId != userId)
            return Forbid();

        try
        {
            await _dbContext.Locations
                .Where(l => l.UserId == userId)
                .ExecuteDeleteAsync();

            _logger.LogInformation("Deleted all locations for user {UserId}", userId);
            return NoContent();
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogError(dbEx, "Error deleting locations for user {UserId}", userId);
            return StatusCode(500, new { message = "Database update error." });
        }
    }
}