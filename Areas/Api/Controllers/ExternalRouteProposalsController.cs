using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Provides thin same-origin generation and acceptance endpoints for external route proposals.</summary>
[Area("Api")]
[ApiController]
[Authorize(Roles = "User")]
[Route("api/trip-editor/{tripId:guid}/segments/{segmentId:guid}/route-proposals")]
public sealed class ExternalRouteProposalsController : ControllerBase
{
    private readonly ExternalRouteProposalGenerator _generator;
    private readonly ExternalRouteProposalAcceptanceService? _acceptance;

    /// <summary>Initializes the thin proposal controller.</summary>
    public ExternalRouteProposalsController(
        ExternalRouteProposalGenerator generator, ExternalRouteProposalAcceptanceService? acceptance = null)
        => (_generator, _acceptance) = (generator, acceptance);

    /// <summary>Generates a proposal from current server-owned Segment and provider context.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(
        Guid tripId, Guid segmentId, ExternalRouteGenerationRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        var result = await _generator.GenerateAsync(userId, tripId, segmentId,
            request.AggregateConcurrencyToken, cancellationToken);
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

    /// <summary>Validates a proposal for copying into this Segment's client draft without persistence.</summary>
    [HttpPost("{proposalId:guid}/accept")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(
        Guid tripId, Guid segmentId, Guid proposalId, ExternalRouteAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (_acceptance == null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        var result = await _acceptance.AcceptAsync(userId, tripId, segmentId, proposalId,
            request.Geometry, request.WaypointIndices, request.ProtectedContext, cancellationToken);
        return result.Succeeded ? Ok(result.Proposal) : Conflict(new ExternalRouteErrorDto(result.ErrorCode!));
    }
}

/// <summary>Contains the only browser-supplied generation input.</summary>
public sealed record ExternalRouteGenerationRequest(string AggregateConcurrencyToken);

/// <summary>Contains one bounded Wayfarer-owned route error code.</summary>
public sealed record ExternalRouteErrorDto(string Code);

/// <summary>Contains the immutable proposal values returned by generation.</summary>
public sealed record ExternalRouteAcceptanceRequest(
    IReadOnlyList<RouteCoordinate> Geometry, IReadOnlyList<int> WaypointIndices, string ProtectedContext);
