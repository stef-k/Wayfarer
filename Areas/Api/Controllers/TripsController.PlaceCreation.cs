using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Rejects the overlapping unconditional creation path; a generated ID cannot make replay safe.</summary>
    [HttpPost("{tripId}/places")]
    public Task<IActionResult> CreatePlace(Guid tripId, [FromBody] PlaceCreateRequestDto request) =>
        Task.FromResult<IActionResult>(GetUserFromToken() == null ? Unauthorized()
            : CreationConditionalRequest.Invalid("Use the conditional Place creation contract.").ToActionResult());
}
