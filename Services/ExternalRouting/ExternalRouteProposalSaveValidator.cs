using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Validates protected proposals inside the Segment Save transaction without writing or provider contact.</summary>
public sealed class ExternalRouteProposalSaveValidator(
    ApplicationDbContext dbContext, SegmentAggregateTokenService aggregateTokens,
    ExternalRouteProposalContextService proposalContexts, AuthoritativeRoutingProviderResolver resolver)
{
    private readonly ApplicationDbContext _dbContext = dbContext;
    private readonly AuthoritativeRoutingProviderResolver _resolver = resolver;

    /// <summary>Locks selection then credential authority before Save takes profile, Segment and anchor locks.</summary>
    internal async Task<(ExternalRouteProposalBinding? Binding, string? Error)> LockAuthorityAsync(
        string userId, Guid tripId, Guid segmentId, EditorSegmentSaveRequest request, CancellationToken cancellationToken)
    {
        var envelope = request.Proposal!;
        if (!proposalContexts.TryRead(envelope.ProtectedContext, out var binding) || binding == null)
            return (null, "route-proposal-invalid-or-expired");
        var geometry = request.Route?.Coordinates.Select(item => new RouteCoordinate(item.X, item.Y)).ToArray();
        var indices = new[] { (int?)0 }.Concat(request.WaypointRouteVertexIndices)
            .Append(geometry?.Length - 1).ToArray();
        if (binding.UserId != userId || binding.TripId != tripId || binding.SegmentId != segmentId
            || binding.ProposalId != envelope.ProposalId || geometry == null || indices.Any(item => !item.HasValue)
            || !GeometryShapeValid(geometry, indices.Select(item => item!.Value).ToArray())
            || binding.GeometryHash != ExternalRouteProposalContextService.GeometryHash(geometry, indices.Select(item => item!.Value).ToArray()))
            return (null, "route-proposal-altered");
        if (binding.ProviderKey != "geoapify" || binding.StorageMode != "persistent")
            return (null, "route-proposal-stale");
        var resolution = await ResolveLockedGeoapifyAsync(userId, binding.MappingMode, cancellationToken);
        var execution = resolution.Execution;
        if (execution == null || execution.ProviderKey != binding.ProviderKey || execution.Profile != binding.MappingMode
            || execution.AuthoritySelectionGeneration != binding.AuthoritySelectionGeneration
            || execution.CredentialGeneration != binding.AuthorityCredentialGeneration
            || execution.RoutingGeneration != binding.AuthorityRoutingGeneration || execution.CatalogVersion != binding.CatalogVersion)
            return (null, "route-proposal-stale");
        return (binding, null);
    }

    /// <summary>Checks locked canonical anchors and the final submitted draft, retaining the original expiry.</summary>
    internal async Task<string?> ValidateFinalAsync(
        ExternalRouteProposalBinding binding, Segment segment, EditorSegmentSaveRequest request,
        Guid? submittedProfileId, CancellationToken cancellationToken)
    {
        if (segment.TransportProfileId != binding.TransportProfileId || submittedProfileId != binding.TransportProfileId
            || !aggregateTokens.TryRead(binding.AggregateConcurrencyToken, segment.UserId, segment.TripId, segment.Id, out var version)
            || version != segment.RowVersion
            || !await _dbContext.Set<TransportProfile>().AsNoTracking().AnyAsync(
                item => item.Id == binding.TransportProfileId && item.IsActive, cancellationToken))
            return "route-proposal-stale";
        var places = new[] { segment.FromPlace }.Concat(segment.Waypoints.OrderBy(item => item.Position).Select(item => item.Place))
            .Concat([segment.ToPlace]).ToArray();
        var submittedIds = new[] { request.FromPlaceId }.Concat(request.WaypointPlaceIds.Select(item => (Guid?)item))
            .Append(request.ToPlaceId);
        if (places.Any(place => place?.Location == null) || !places.Select(place => (Guid?)place!.Id).SequenceEqual(submittedIds))
            return "route-proposal-altered";
        var anchors = places.Select(place => new RouteCoordinate(place!.Location!.X, place.Location.Y)).ToArray();
        var indices = new[] { 0 }.Concat(request.WaypointRouteVertexIndices.Select(item => item!.Value))
            .Append(request.Route!.NumPoints - 1).ToArray();
        if (ExternalRouteAnchorFingerprint.Compute(places!, anchors) != binding.AnchorFingerprint
            || indices.Where((index, anchorIndex) => new RouteCoordinate(request.Route.GetCoordinateN(index).X,
                request.Route.GetCoordinateN(index).Y) != anchors[anchorIndex]).Any())
            return "route-proposal-stale";
        return IsCurrent(binding, request.Proposal!.ProtectedContext) ? null : "route-proposal-invalid-or-expired";
    }

    /// <summary>Rechecks original token expiry immediately before the durable write.</summary>
    internal bool IsCurrent(ExternalRouteProposalBinding binding, string protectedContext) =>
        proposalContexts.TryRead(protectedContext, out var current) && current != null
        && binding with { Instructions = null } == current with { Instructions = null }
        && (binding.Instructions == null) == (current.Instructions == null)
        && (binding.Instructions?.SequenceEqual(current.Instructions!) ?? true);

    /// <summary>Locks personal routing selection before its selected profile and resolves only those locked rows.</summary>
    private async Task<RoutingProviderResolutionResult> ResolveLockedGeoapifyAsync(
        string userId, string? nativeMode, CancellationToken cancellationToken)
    {
        var selection = _dbContext.Database.IsNpgsql()
            ? await _dbContext.Set<PersonalLocationProviderSelection>().FromSqlInterpolated($$"""
                SELECT *, xmin FROM "PersonalLocationProviderSelections"
                WHERE "UserId" = {{userId}} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await _dbContext.Set<PersonalLocationProviderSelection>()
                .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (selection?.RoutingProviderKey != "geoapify")
            return RoutingProviderResolutionResult.Unavailable("no-provider-selected");
        var profile = _dbContext.Database.IsNpgsql()
            ? await _dbContext.Set<PersonalLocationProviderProfile>().FromSqlInterpolated($$"""
                SELECT *, xmin FROM "PersonalLocationProviderProfiles"
                WHERE "UserId" = {{userId}} AND "ProviderKey" = {{selection.RoutingProviderKey}} FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await _dbContext.Set<PersonalLocationProviderProfile>().SingleOrDefaultAsync(
                item => item.UserId == userId && item.ProviderKey == selection.RoutingProviderKey, cancellationToken);
        return _resolver.ResolveLockedNative(selection, profile, nativeMode);
    }

    private static bool GeometryShapeValid(IReadOnlyList<RouteCoordinate> geometry, IReadOnlyList<int> indices) =>
        geometry.Count is >= 2 and <= 1000 && geometry.All(item => item.IsValid)
        && indices.Count >= 2 && indices[0] == 0 && indices[^1] == geometry.Count - 1
        && indices.All(index => index >= 0 && index < geometry.Count)
        && indices.Zip(indices.Skip(1), (first, second) => second > first).All(value => value);
}
