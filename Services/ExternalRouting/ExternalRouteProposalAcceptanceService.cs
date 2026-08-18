using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Validates protected proposal context without provider contact or persistence.</summary>
public sealed class ExternalRouteProposalAcceptanceService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly SegmentAggregateTokenService _aggregateTokens;
    private readonly ExternalRouteProposalContextService _proposalContexts;

    /// <summary>Initializes server-authoritative proposal acceptance.</summary>
    public ExternalRouteProposalAcceptanceService(
        ApplicationDbContext dbContext, SegmentAggregateTokenService aggregateTokens,
        ExternalRouteProposalContextService proposalContexts)
        => (_dbContext, _aggregateTokens, _proposalContexts) = (dbContext, aggregateTokens, proposalContexts);

    /// <summary>Returns an accepted draft value only if geometry and every stale dimension remain authoritative.</summary>
    public async Task<ExternalRouteAcceptanceResult> AcceptAsync(
        string userId, Guid tripId, Guid segmentId, Guid proposalId, IReadOnlyList<RouteCoordinate> geometry,
        IReadOnlyList<int> waypointIndices, string protectedContext, CancellationToken cancellationToken)
    {
        if (!_proposalContexts.TryRead(protectedContext, out var binding) || binding == null)
            return ExternalRouteAcceptanceResult.Failure("route-proposal-invalid-or-expired");
        if (binding.UserId != userId || binding.TripId != tripId || binding.SegmentId != segmentId
            || binding.ProposalId != proposalId || binding.GeometryHash != ExternalRouteProposalContextService.GeometryHash(geometry, waypointIndices))
            return ExternalRouteAcceptanceResult.Failure("route-proposal-altered");
        if (!GeometryShapeValid(geometry, waypointIndices))
            return ExternalRouteAcceptanceResult.Failure("route-proposal-altered");

        var settings = await _dbContext.ApplicationSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings?.ExternalRouteGenerationEnabled != true
            || settings.ExternalRouteGenerationVersion != binding.FeatureStateGeneration
            || settings.ActiveRoutingProviderConfigurationId != binding.ProviderId)
            return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");
        var providerCurrent = await _dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .AnyAsync(item => item.Id == binding.ProviderId && item.Enabled
                && item.ConfigurationVersion == binding.ProviderConfigurationVersion
                && item.VerifiedConfigurationVersion == item.ConfigurationVersion,
                cancellationToken);
        if (!providerCurrent) return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");

        var segment = await _dbContext.Set<Segment>().AsNoTracking()
            .Include(item => item.FromPlace).Include(item => item.ToPlace)
            .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position)).ThenInclude(item => item.Place)
            .SingleOrDefaultAsync(item => item.Id == segmentId && item.TripId == tripId && item.UserId == userId, cancellationToken);
        if (segment?.TransportProfileId != binding.TransportProfileId
            || !_aggregateTokens.TryRead(binding.AggregateConcurrencyToken, userId, tripId, segmentId, out var rowVersion)
            || rowVersion != segment.RowVersion) return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");
        var places = new[] { segment.FromPlace }.Concat(segment.Waypoints.OrderBy(item => item.Position).Select(item => item.Place))
            .Concat([segment.ToPlace]).ToArray();
        if (places.Any(place => place?.Location == null) || places.Length != waypointIndices.Count)
            return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");
        var anchors = places.Select(place => new RouteCoordinate(place!.Location!.X, place.Location.Y)).ToArray();
        if (ExternalRouteAnchorFingerprint.Compute(places!, anchors) != binding.AnchorFingerprint
            || waypointIndices.Where((index, anchorIndex) => geometry[index] != anchors[anchorIndex]).Any())
            return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");

        return new ExternalRouteAcceptanceResult(true, null,
            new AcceptedExternalRouteProposalDto(proposalId, segmentId, geometry, waypointIndices));
    }

    private static bool GeometryShapeValid(IReadOnlyList<RouteCoordinate> geometry, IReadOnlyList<int> indices) =>
        geometry.Count is >= 2 and <= 1000 && geometry.All(item => item.IsValid)
        && indices.Count >= 2 && indices[0] == 0 && indices[^1] == geometry.Count - 1
        && indices.All(index => index >= 0 && index < geometry.Count)
        && indices.Zip(indices.Skip(1), (first, second) => second > first).All(value => value);
}

/// <summary>Contains a validated proposal suitable only for copying into one client draft.</summary>
public sealed record AcceptedExternalRouteProposalDto(
    Guid ProposalId, Guid SegmentId, IReadOnlyList<RouteCoordinate> Geometry, IReadOnlyList<int> WaypointIndices);

/// <summary>Contains a bounded acceptance outcome without persistence.</summary>
public sealed record ExternalRouteAcceptanceResult(
    bool Succeeded, string? ErrorCode, AcceptedExternalRouteProposalDto? Proposal = null)
{
    /// <summary>Creates a safe acceptance failure.</summary>
    public static ExternalRouteAcceptanceResult Failure(string code) => new(false, code);
}
