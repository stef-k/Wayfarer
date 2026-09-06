using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Provides thin same-origin generation endpoint for external route proposals.</summary>
[Area("Api")]
[ApiController]
[Authorize(Roles = "User")]
[Route("api/trip-editor/{tripId:guid}/segments/{segmentId:guid}/route-proposals")]
public sealed class ExternalRouteProposalsController : ControllerBase
{
    private readonly ExternalRouteProposalGenerator _generator;
    /// <summary>Initializes the thin proposal controller.</summary>
    public ExternalRouteProposalsController(ExternalRouteProposalGenerator generator) => _generator = generator;

    /// <summary>Generates a proposal from current server-owned Segment and provider context.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(
        Guid tripId, Guid segmentId, ExternalRouteGenerationRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (request.ProviderMode == null)
            return UnprocessableEntity(new ExternalRouteErrorDto("provider-mode-required"));
        var result = await _generator.GenerateAsync(userId, tripId, segmentId,
            request.AggregateConcurrencyToken, request.ProviderMode, cancellationToken);
        if (result.Succeeded) return Ok(result.Proposal);
        return result.ErrorCode switch
        {
            "segment-not-found" => NotFound(new ExternalRouteErrorDto(result.ErrorCode)),
            "segment-aggregate-stale" or "route-proposal-context-stale" =>
                Conflict(new ExternalRouteErrorDto(result.ErrorCode)),
            "routing-budget-exhausted" or "provider-rate-limited" =>
                StatusCode(StatusCodes.Status429TooManyRequests, new ExternalRouteErrorDto(result.ErrorCode)),
            _ => UnprocessableEntity(new ExternalRouteErrorDto(result.ErrorCode ?? "external-routing-unavailable"))
        };
    }

}

/// <summary>Contains the only browser-supplied generation input.</summary>
public sealed record ExternalRouteGenerationRequest(string AggregateConcurrencyToken, string? ProviderMode = null);

/// <summary>Contains one bounded Wayfarer-owned route error code.</summary>
public sealed record ExternalRouteErrorDto(string Code);
