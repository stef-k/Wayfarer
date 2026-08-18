using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Generates immutable route proposals from fully reloaded server authority.</summary>
public sealed class ExternalRouteProposalGenerator
{
    private readonly Func<ApplicationSettings>? _settingsForGateTest;
    private readonly ApplicationDbContext? _dbContext;
    private readonly SegmentAggregateTokenService? _aggregateTokens;
    private readonly IOsrmRouteClient? _client;
    private readonly IProviderRouteGeometryValidator? _geometryValidator;
    private readonly ExternalRouteProposalContextService? _proposalContexts;
    private readonly RoutingRequestBudget? _budgets;

    /// <summary>Initializes the narrow feature-gate seam used by configuration contract tests.</summary>
    public ExternalRouteProposalGenerator(Func<ApplicationSettings> settings) => _settingsForGateTest = settings;

    /// <summary>Initializes authoritative proposal generation.</summary>
    public ExternalRouteProposalGenerator(
        ApplicationDbContext dbContext, SegmentAggregateTokenService aggregateTokens, IOsrmRouteClient client,
        IProviderRouteGeometryValidator geometryValidator, ExternalRouteProposalContextService proposalContexts,
        RoutingRequestBudget budgets)
        => (_dbContext, _aggregateTokens, _client, _geometryValidator, _proposalContexts, _budgets)
            = (dbContext, aggregateTokens, client, geometryValidator, proposalContexts, budgets);

    /// <summary>Rejects disabled generation without contacting a provider.</summary>
    public Task<ExternalRouteGenerationResult> GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_settingsForGateTest!().ExternalRouteGenerationEnabled
            ? ExternalRouteGenerationResult.Failure("external-routing-unavailable")
            : ExternalRouteGenerationResult.Failure("external-routing-disabled"));
    }

    /// <summary>Reloads every authoritative input and returns a non-persisted immutable proposal.</summary>
    public async Task<ExternalRouteGenerationResult> GenerateAsync(
        string userId, Guid tripId, Guid segmentId, string aggregateConcurrencyToken, CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(userId, tripId, segmentId, aggregateConcurrencyToken, cancellationToken);
        if (!context.Succeeded) return ExternalRouteGenerationResult.Failure(context.ErrorCode!);
        using var lease = await _budgets!.AcquireAsync(userId, context.Provider!.Id,
            context.Provider.RequestsPerMinute, context.Provider.MaxConcurrency, cancellationToken);
        if (lease == null) return ExternalRouteGenerationResult.Failure("routing-budget-exhausted");

        var providerResult = await _client!.RouteAsync(context.Provider, context.OsrmProfile!, context.Anchors!, lease, cancellationToken);
        if (!providerResult.Succeeded) return ExternalRouteGenerationResult.Failure(providerResult.ErrorCode!);
        var validated = _geometryValidator!.Validate(context.Anchors!, providerResult, cancellationToken);
        if (!validated.Succeeded) return ExternalRouteGenerationResult.Failure(validated.ErrorCode!);

        var finalContext = await LoadContextAsync(userId, tripId, segmentId, aggregateConcurrencyToken, cancellationToken);
        if (!finalContext.Succeeded || finalContext.Fingerprint != context.Fingerprint
            || finalContext.Provider!.Id != context.Provider.Id
            || finalContext.Provider.ConfigurationVersion != context.Provider.ConfigurationVersion
            || finalContext.FeatureStateGeneration != context.FeatureStateGeneration)
            return ExternalRouteGenerationResult.Failure("route-proposal-context-stale");

        var proposalId = Guid.NewGuid();
        var geometryHash = ExternalRouteProposalContextService.GeometryHash(validated.Geometry!, validated.WaypointIndices!);
        var binding = new ExternalRouteProposalBinding(
            proposalId, tripId, segmentId, userId, geometryHash, context.Fingerprint!, context.TransportProfileId!.Value,
            context.Provider.Id, context.Provider.ConfigurationVersion, context.FeatureStateGeneration,
            aggregateConcurrencyToken);
        var protectedContext = _proposalContexts!.Issue(binding);
        var proposal = new ExternalRouteProposalDto(proposalId, segmentId, validated.Geometry!, validated.WaypointIndices!,
            protectedContext.Token, protectedContext.ExpiresAt);
        return new ExternalRouteGenerationResult(true, null, proposal);
    }

    private async Task<GenerationContext> LoadContextAsync(
        string userId, Guid tripId, Guid segmentId, string aggregateToken, CancellationToken cancellationToken)
    {
        var settings = await _dbContext!.ApplicationSettings.AsNoTracking()
            .Include(item => item.ActiveRoutingProviderConfiguration)!
            .ThenInclude(item => item!.ProfileMappings)
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (settings?.ExternalRouteGenerationEnabled != true) return GenerationContext.Failure("external-routing-disabled");
        var provider = settings.ActiveRoutingProviderConfiguration;
        if (provider == null || !provider.Enabled || provider.VerifiedConfigurationVersion != provider.ConfigurationVersion)
            return GenerationContext.Failure("external-routing-unavailable");

        var segment = await _dbContext.Set<Segment>().AsNoTracking()
            .Include(item => item.FromPlace).Include(item => item.ToPlace)
            .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position)).ThenInclude(item => item.Place)
            .SingleOrDefaultAsync(item => item.Id == segmentId && item.TripId == tripId && item.UserId == userId, cancellationToken);
        if (segment == null) return GenerationContext.Failure("segment-not-found");
        if (!_aggregateTokens!.TryRead(aggregateToken, userId, tripId, segmentId, out var rowVersion)
            || rowVersion != segment.RowVersion) return GenerationContext.Failure("segment-aggregate-stale");
        if (segment.TransportProfileId is not { } transportProfileId)
            return GenerationContext.Failure("routing-profile-unavailable");
        var mapping = provider.ProfileMappings.SingleOrDefault(item => item.TransportProfileId == transportProfileId);
        if (mapping == null) return GenerationContext.Failure("routing-profile-unavailable");
        var places = new[] { segment.FromPlace }.Concat(segment.Waypoints.OrderBy(item => item.Position).Select(item => item.Place))
            .Concat([segment.ToPlace]).ToArray();
        if (places.Length is < 2 or > 50 || places.Any(place => place?.Location == null))
            return GenerationContext.Failure("segment-anchors-invalid");
        var anchors = places.Select(place => new RouteCoordinate(place!.Location!.X, place.Location.Y)).ToArray();
        var fingerprint = ExternalRouteAnchorFingerprint.Compute(places!, anchors);
        return new GenerationContext(true, null, provider, mapping.OsrmProfile, anchors, transportProfileId,
            settings.ExternalRouteGenerationVersion, fingerprint);
    }

    private sealed record GenerationContext(
        bool Succeeded, string? ErrorCode, RoutingProviderConfiguration? Provider = null, string? OsrmProfile = null,
        IReadOnlyList<RouteCoordinate>? Anchors = null, Guid? TransportProfileId = null,
        int FeatureStateGeneration = 0, string? Fingerprint = null)
    {
        public static GenerationContext Failure(string code) => new(false, code);
    }
}

/// <summary>Contains a bounded Wayfarer-owned generation outcome.</summary>
public sealed record ExternalRouteGenerationResult(bool Succeeded, string? ErrorCode, ExternalRouteProposalDto? Proposal = null)
{
    /// <summary>Creates a failure without provider details.</summary>
    public static ExternalRouteGenerationResult Failure(string code) => new(false, code);
}

/// <summary>Contains an immutable, non-persisted route proposal for explicit acceptance.</summary>
public sealed record ExternalRouteProposalDto(
    Guid ProposalId, Guid SegmentId, IReadOnlyList<RouteCoordinate> Geometry, IReadOnlyList<int> WaypointIndices,
    string ProtectedContext, DateTimeOffset ExpiresAt);

/// <summary>Validates and budgets untrusted provider geometry while preserving every exact anchor.</summary>
public interface IProviderRouteGeometryValidator
{
    /// <summary>Returns a complete protected-anchor route or a bounded failure.</summary>
    ProviderRouteValidationResult Validate(
        IReadOnlyList<RouteCoordinate> anchors, OsrmRouteResult providerRoute, CancellationToken cancellationToken);
}

/// <summary>Contains validated budgeted geometry and complete waypoint indices.</summary>
public sealed record ProviderRouteValidationResult(
    bool Succeeded, IReadOnlyList<RouteCoordinate>? Geometry, IReadOnlyList<int>? WaypointIndices, string? ErrorCode)
{
    /// <summary>Creates a bounded validation failure.</summary>
    public static ProviderRouteValidationResult Failure(string code) => new(false, null, null, code);
}
