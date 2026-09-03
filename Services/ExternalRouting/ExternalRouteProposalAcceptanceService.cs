using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NetTopologySuite.Geometries;
using System.Text.Json;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Validates protected proposal context without provider contact or persistence.</summary>
public sealed class ExternalRouteProposalAcceptanceService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly SegmentAggregateTokenService _aggregateTokens;
    private readonly ExternalRouteProposalContextService _proposalContexts;
    private readonly AuthoritativeRoutingProviderResolver _resolver;

    /// <summary>Initializes server-authoritative proposal acceptance.</summary>
    public ExternalRouteProposalAcceptanceService(
        ApplicationDbContext dbContext, SegmentAggregateTokenService aggregateTokens,
        ExternalRouteProposalContextService proposalContexts, AuthoritativeRoutingProviderResolver resolver)
        => (_dbContext, _aggregateTokens, _proposalContexts, _resolver) =
            (dbContext, aggregateTokens, proposalContexts, resolver);

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

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        try
        {
            var result = await ValidateAuthorityAsync(
                userId, tripId, segmentId, proposalId, geometry, waypointIndices, binding, cancellationToken);
            if (result.Succeeded
                && (!_proposalContexts.TryRead(protectedContext, out var finalBinding)
                    || !SameBinding(binding, finalBinding)))
                result = ExternalRouteAcceptanceResult.Failure("route-proposal-invalid-or-expired");
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");
        }
    }

    /// <summary>Compares every protected member while treating deserialized instructions as values.</summary>
    private static bool SameBinding(ExternalRouteProposalBinding expected, ExternalRouteProposalBinding? actual) =>
        actual != null
        && expected with { Instructions = null } == actual with { Instructions = null }
        && (expected.Instructions == null) == (actual.Instructions == null)
        && (expected.Instructions?.SequenceEqual(actual.Instructions!) ?? true);

    private async Task<ExternalRouteAcceptanceResult> ValidateAuthorityAsync(
        string userId, Guid tripId, Guid segmentId, Guid proposalId, IReadOnlyList<RouteCoordinate> geometry,
        IReadOnlyList<int> waypointIndices, ExternalRouteProposalBinding binding, CancellationToken cancellationToken)
    {
        var relational = _dbContext.Database.IsRelational();
        if (binding.ProviderKey != "geoapify"
            && !await LegacyAuthorityCurrentAsync(userId, binding, relational, cancellationToken))
            return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");
        var resolution = binding.ProviderKey == "geoapify"
            ? await _resolver.ResolveNativeAsync(userId, binding.MappingMode, cancellationToken)
            : await _resolver.ResolveAsync(userId, binding.TransportProfileId, cancellationToken);
        var execution = resolution.Execution;
        if (execution == null || execution.SelectionMode != binding.ProviderSelectionMode
            || execution.UserConfigurationVersion != binding.UserRoutingConfigurationVersion
            || execution.Provider.Id != binding.ProviderId
            || execution.ProviderConfigurationVersion != binding.ProviderConfigurationVersion
            || execution.FeatureStateGeneration != binding.FeatureStateGeneration)
            return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");

        var profile = await _dbContext.Set<TransportProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == binding.TransportProfileId, cancellationToken);
        if (profile is not { IsActive: true }) return ExternalRouteAcceptanceResult.Failure("route-proposal-stale");

        if (relational)
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Segments\" WHERE \"Id\" = {segmentId} AND \"TripId\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE",
                cancellationToken);
        var segment = await _dbContext.Set<Segment>()
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

        var aggregateConcurrencyToken = binding.AggregateConcurrencyToken;
        if (binding.ProviderKey == "geoapify" && binding.StorageMode == "persistent")
        {
            segment.RouteGeometry = new LineString(geometry.Select(item => new Coordinate(item.Longitude, item.Latitude)).ToArray()) { SRID = 4326 };
            segment.EstimatedDistanceKm = binding.DistanceMetres / 1000d;
            segment.EstimatedDuration = binding.DurationSeconds.HasValue
                ? TimeSpan.FromSeconds(binding.DurationSeconds.Value) : null;
            segment.EstimatedDurationSource = EstimatedDurationSource.Automatic;
            segment.RouteInstructionsJson = JsonSerializer.Serialize(binding.Instructions ?? []);
            segment.RouteProvider = binding.ProviderKey;
            segment.RouteProviderConfigurationId = null;
            segment.RouteProviderConfigurationVersion = null;
            segment.RouteTransportProfileId = binding.TransportProfileId;
            segment.RouteMappingMode = binding.MappingMode;
            segment.RouteGeneratedAt = binding.GeneratedAt?.ToUniversalTime();
            segment.RouteAttribution = binding.Attribution;
            segment.RouteStorageMode = binding.StorageMode;
            await _dbContext.SaveChangesAsync(cancellationToken);
            aggregateConcurrencyToken = _aggregateTokens.Issue(userId, tripId, segmentId, segment.RowVersion);
        }
        return new ExternalRouteAcceptanceResult(true, null,
            new AcceptedExternalRouteProposalDto(proposalId, segmentId, geometry, waypointIndices,
                binding.DistanceMetres, binding.DurationSeconds, binding.Instructions, binding.ProviderKey,
                binding.Attribution, binding.StorageMode, aggregateConcurrencyToken));
    }

    private async Task<bool> LegacyAuthorityCurrentAsync(string userId, ExternalRouteProposalBinding binding,
        bool relational, CancellationToken cancellationToken)
    {
        if (relational)
            await _dbContext.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"ApplicationSettings\" WHERE \"Id\" = 1 FOR UPDATE", cancellationToken);
        var settings = await _dbContext.ApplicationSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings?.ExternalRouteGenerationEnabled != true
            || settings.ExternalRouteGenerationVersion != binding.FeatureStateGeneration
            || binding.ProviderSelectionMode == RoutingProviderSelectionMode.ServerDefault
                && settings.ActiveRoutingProviderConfigurationId != binding.ProviderId) return false;
        if (relational)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"RoutingProviderConfigurations\" WHERE \"Id\" = {binding.ProviderId} FOR UPDATE",
                cancellationToken);
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"UserRoutingConfigurations\" WHERE \"UserId\" = {userId} FOR UPDATE",
                cancellationToken);
        }
        var provider = await _dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == binding.ProviderId, cancellationToken);
        if (provider is not { Enabled: true }
            || provider.ConfigurationVersion != binding.ProviderConfigurationVersion
            || provider.VerifiedConfigurationVersion != provider.ConfigurationVersion) return false;
        return await _dbContext.Set<RoutingProviderProfileMapping>().AsNoTracking()
            .AnyAsync(item => item.RoutingProviderConfigurationId == binding.ProviderId
                && item.TransportProfileId == binding.TransportProfileId, cancellationToken);
    }

    private static bool GeometryShapeValid(IReadOnlyList<RouteCoordinate> geometry, IReadOnlyList<int> indices) =>
        geometry.Count is >= 2 and <= 1000 && geometry.All(item => item.IsValid)
        && indices.Count >= 2 && indices[0] == 0 && indices[^1] == geometry.Count - 1
        && indices.All(index => index >= 0 && index < geometry.Count)
        && indices.Zip(indices.Skip(1), (first, second) => second > first).All(value => value);
}

/// <summary>Contains a validated proposal suitable only for copying into one client draft.</summary>
public sealed record AcceptedExternalRouteProposalDto(
    Guid ProposalId, Guid SegmentId, IReadOnlyList<RouteCoordinate> Geometry, IReadOnlyList<int> WaypointIndices,
    double? DistanceMetres = null, double? DurationSeconds = null, IReadOnlyList<RouteInstruction>? Instructions = null,
    string? Provider = null, string? Attribution = null, string? StorageMode = null,
    string? AggregateConcurrencyToken = null);

/// <summary>Contains a bounded acceptance outcome without persistence.</summary>
public sealed record ExternalRouteAcceptanceResult(
    bool Succeeded, string? ErrorCode, AcceptedExternalRouteProposalDto? Proposal = null)
{
    /// <summary>Creates a safe acceptance failure.</summary>
    public static ExternalRouteAcceptanceResult Failure(string code) => new(false, code);
}
