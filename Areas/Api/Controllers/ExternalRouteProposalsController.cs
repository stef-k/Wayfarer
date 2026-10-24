using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        Guid tripId, Guid segmentId, JsonElement body, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        if (!ExternalRouteGenerationRequest.TryParse(body, out var request))
            return BadRequest(new ExternalRouteErrorDto("route-proposal-request-invalid"));
        if (request!.ProviderMode == null)
            return UnprocessableEntity(new ExternalRouteErrorDto("provider-mode-required"));
        var result = await _generator.GenerateAsync(userId, tripId, segmentId,
            request.OriginalRouteToken ?? request.AggregateConcurrencyToken!, request.ProviderMode, cancellationToken, request.OriginalRouteToken != null);
        if (result.Succeeded) return Ok(result.Proposal);
        return result.ErrorCode switch
        {
            "segment-not-found" => NotFound(new ExternalRouteErrorDto(result.ErrorCode)),
            "segment-route-stale" or "segment-aggregate-stale" or "route-proposal-context-stale" =>
                Conflict(new ExternalRouteErrorDto(result.ErrorCode)),
            "routing-budget-exhausted" or "provider-rate-limited" =>
                StatusCode(StatusCodes.Status429TooManyRequests, new ExternalRouteErrorDto(result.ErrorCode)),
            _ => UnprocessableEntity(new ExternalRouteErrorDto(result.ErrorCode ?? "external-routing-unavailable"))
        };
    }

}

/// <summary>Contains the only browser-supplied generation input.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalRouteGenerationRequest(string? AggregateConcurrencyToken, string? ProviderMode = null,
    string? Contract = null, string? OriginalRouteToken = null)
{
    /// <summary>Rejects duplicate/unknown fields and unsupported dialects instead of inferring protection.</summary>
    public static bool TryParse(JsonElement body, out ExternalRouteGenerationRequest? request)
    {
        request = null;
        if (body.ValueKind != JsonValueKind.Object) return false;
        try
        {
            request = body.Deserialize<ExternalRouteGenerationRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false });
            if (request == null) return false;
            var independent = body.TryGetProperty("contract", out _) || body.TryGetProperty("originalRouteToken", out _);
            return independent
                ? request.Contract == "segment-route-notes-1" && !string.IsNullOrWhiteSpace(request.OriginalRouteToken)
                    && !body.TryGetProperty("aggregateConcurrencyToken", out _)
                : !string.IsNullOrWhiteSpace(request.AggregateConcurrencyToken);
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>Contains one bounded Wayfarer-owned route error code.</summary>
public sealed record ExternalRouteErrorDto(string Code);
