using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Generates immutable route proposals from fully reloaded server authority.</summary>
public sealed class ExternalRouteProposalGenerator
{
    private readonly ApplicationDbContext? _dbContext;
    private readonly SegmentAggregateTokenService? _aggregateTokens;
    private readonly IProviderRouteClient? _client;
    private readonly IProviderRouteGeometryValidator? _geometryValidator;
    private readonly ExternalRouteProposalContextService? _proposalContexts;
    private readonly RoutingRequestBudget? _budgets;
    private readonly AuthoritativeRoutingProviderResolver? _resolver;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>Initializes authoritative proposal generation.</summary>
    public ExternalRouteProposalGenerator(
        ApplicationDbContext dbContext, SegmentAggregateTokenService aggregateTokens, IProviderRouteClient client,
        IProviderRouteGeometryValidator geometryValidator, ExternalRouteProposalContextService proposalContexts,
        RoutingRequestBudget budgets, AuthoritativeRoutingProviderResolver resolver, TimeProvider? timeProvider = null)
    {
        (_dbContext, _aggregateTokens, _client, _geometryValidator, _proposalContexts, _budgets)
            = (dbContext, aggregateTokens, client, geometryValidator, proposalContexts, budgets);
        _resolver = resolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Reloads every authoritative input and returns a non-persisted immutable proposal.</summary>
    public Task<ExternalRouteGenerationResult> GenerateAsync(
        string userId, Guid tripId, Guid segmentId, string aggregateConcurrencyToken,
        CancellationToken cancellationToken) =>
        GenerateAsync(userId, tripId, segmentId, aggregateConcurrencyToken, null, cancellationToken);

    /// <summary>Reloads authoritative inputs for one explicitly selected provider-native mode.</summary>
    public async Task<ExternalRouteGenerationResult> GenerateAsync(
        string userId, Guid tripId, Guid segmentId, string aggregateConcurrencyToken, string? providerMode,
        CancellationToken cancellationToken)
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var operationTimer = _timeProvider.CreateTimer(
            _ => operationTimeout.Cancel(), null, TimeSpan.FromSeconds(300), Timeout.InfiniteTimeSpan);
        try
        {
            var result = await GenerateCoreAsync(
                userId, tripId, segmentId, aggregateConcurrencyToken, providerMode, operationTimeout.Token);
            return operationTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? ExternalRouteGenerationResult.Failure("routing-timeout") : result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return ExternalRouteGenerationResult.Failure("routing-timeout"); }
        catch (OperationCanceledException)
        { return ExternalRouteGenerationResult.Failure("request-cancelled"); }
    }

    private async Task<ExternalRouteGenerationResult> GenerateCoreAsync(
        string userId, Guid tripId, Guid segmentId, string aggregateConcurrencyToken, string? providerMode,
        CancellationToken operationToken)
    {
        if (providerMode != null && !ProviderDirectionsCatalog.TryParse("geoapify", providerMode, out _))
            return ExternalRouteGenerationResult.Failure("unsupported-provider-mode");
        var context = await LoadContextAsync(userId, tripId, segmentId, aggregateConcurrencyToken, providerMode, operationToken);
        if (!context.Succeeded) return ExternalRouteGenerationResult.Failure(context.ErrorCode!);
        if (RoutingProviderAnchorPolicy.Validate(context.Execution!, context.Anchors!) is { } anchorError)
            return ExternalRouteGenerationResult.Failure(anchorError);
        if (!_budgets!.TryAdmitUserGeneration(userId))
            return ExternalRouteGenerationResult.Failure("routing-budget-exhausted");

        var providerResult = await _client!.RouteAsync(context.Execution!, context.Anchors!,
            token => IsCurrentAsync(context, userId, tripId, segmentId, aggregateConcurrencyToken, providerMode, token), operationToken);
        if (!providerResult.Succeeded) return ExternalRouteGenerationResult.Failure(providerResult.ErrorCode!);
        var validated = _geometryValidator!.Validate(context.Anchors!, providerResult, operationToken);
        if (!validated.Succeeded) return ExternalRouteGenerationResult.Failure(validated.ErrorCode!);

        var finalContext = await LoadContextAsync(userId, tripId, segmentId, aggregateConcurrencyToken, providerMode, operationToken);
        if (!finalContext.Succeeded || finalContext.Fingerprint != context.Fingerprint
            || finalContext.TransportProfileId != context.TransportProfileId
            || !SameAuthority(finalContext.Execution, context.Execution))
            return ExternalRouteGenerationResult.Failure("route-proposal-context-stale");

        var proposalId = Guid.NewGuid();
        var geometryHash = ExternalRouteProposalContextService.GeometryHash(validated.Geometry!, validated.WaypointIndices!);
        var binding = new ExternalRouteProposalBinding(
            proposalId, tripId, segmentId, userId, geometryHash, context.Fingerprint!, context.TransportProfileId,
            aggregateConcurrencyToken,
            providerResult.DistanceMetres, providerResult.DurationSeconds, providerResult.Instructions,
            context.Execution!.ProviderKey,
            context.Execution.Profile, _timeProvider.GetUtcNow(),
            context.Execution.Attribution, "persistent", context.Execution.AuthoritySelectionGeneration,
            context.Execution.CredentialGeneration, context.Execution.RoutingGeneration,
            context.Execution.CatalogVersion);
        var protectedContext = _proposalContexts!.Issue(binding);
        var proposal = new ExternalRouteProposalDto(proposalId, segmentId, validated.Geometry!, validated.WaypointIndices!,
            protectedContext.Token, protectedContext.ExpiresAt, providerResult.DistanceMetres,
            providerResult.DurationSeconds, providerResult.Instructions,
            context.Execution.ProviderKey,
            context.Execution.Attribution);
        return new ExternalRouteGenerationResult(true, null, proposal);
    }

    private async Task<bool> IsCurrentAsync(
        GenerationContext original, string userId, Guid tripId, Guid segmentId, string aggregateToken,
        string? providerMode, CancellationToken cancellationToken)
    {
        var current = await LoadContextAsync(userId, tripId, segmentId, aggregateToken, providerMode, cancellationToken);
        return current.Succeeded && SameAuthority(current.Execution, original.Execution)
            && current.TransportProfileId == original.TransportProfileId && current.Fingerprint == original.Fingerprint;
    }

    private async Task<GenerationContext> LoadContextAsync(
        string userId, Guid tripId, Guid segmentId, string aggregateToken, string? providerMode,
        CancellationToken cancellationToken)
    {
        var segment = await _dbContext!.Set<Segment>().AsNoTracking()
            .Include(item => item.FromPlace).Include(item => item.ToPlace)
            .Include(item => item.Waypoints.OrderBy(waypoint => waypoint.Position)).ThenInclude(item => item.Place)
            .SingleOrDefaultAsync(item => item.Id == segmentId && item.TripId == tripId && item.UserId == userId, cancellationToken);
        if (segment == null) return GenerationContext.Failure("segment-not-found");
        if (!_aggregateTokens!.TryRead(aggregateToken, userId, tripId, segmentId, out var rowVersion)
            || rowVersion != segment.RowVersion) return GenerationContext.Failure("segment-aggregate-stale");
        // Planning identity is retained for stale checks, not provider routing eligibility.
        if (providerMode == null) return GenerationContext.Failure("provider-mode-required");
        var resolution = await _resolver!.ResolveNativeAsync(userId, providerMode, cancellationToken);
        if (resolution.Execution == null) return GenerationContext.Failure(resolution.ErrorCode ?? "external-routing-unavailable");
        var places = new[] { segment.FromPlace }.Concat(segment.Waypoints.OrderBy(item => item.Position).Select(item => item.Place))
            .Concat([segment.ToPlace]).ToArray();
        if (places.Length is < 2 or > 50 || places.Any(place => place?.Location == null))
            return GenerationContext.Failure("segment-anchors-invalid");
        var anchors = places.Select(place => new RouteCoordinate(place!.Location!.X, place.Location.Y)).ToArray();
        var fingerprint = ExternalRouteAnchorFingerprint.Compute(places!, anchors);
        return new GenerationContext(true, null, resolution.Execution, anchors, segment.TransportProfileId, fingerprint);
    }

    private static bool SameAuthority(ResolvedRoutingProviderExecution? first, ResolvedRoutingProviderExecution? second) =>
        first != null && second != null && first.ProviderKey == second.ProviderKey
        && first.Profile == second.Profile
        && first.AuthoritySelectionGeneration == second.AuthoritySelectionGeneration
        && first.CredentialGeneration == second.CredentialGeneration
        && first.RoutingGeneration == second.RoutingGeneration
        && first.CatalogVersion == second.CatalogVersion
        && first.RoutingAuthorized == second.RoutingAuthorized
        && first.RoutingVerification == second.RoutingVerification
        && first.VerifiedCredentialGeneration == second.VerifiedCredentialGeneration
        && first.VerifiedRoutingGeneration == second.VerifiedRoutingGeneration;

    private sealed record GenerationContext(
        bool Succeeded, string? ErrorCode, ResolvedRoutingProviderExecution? Execution = null,
        IReadOnlyList<RouteCoordinate>? Anchors = null, Guid? TransportProfileId = null, string? Fingerprint = null)
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

/// <summary>Contains an immutable, non-persisted route proposal for explicit Segment Save.</summary>
public sealed record ExternalRouteProposalDto(
    Guid ProposalId, Guid SegmentId, IReadOnlyList<RouteCoordinate> Geometry, IReadOnlyList<int> WaypointIndices,
    string ProtectedContext, DateTimeOffset ExpiresAt, double? DistanceMetres = null, double? DurationSeconds = null,
    IReadOnlyList<RouteInstruction>? Instructions = null, string? Provider = null, string? Attribution = null);

/// <summary>Validates and budgets untrusted provider geometry while preserving every exact anchor.</summary>
public interface IProviderRouteGeometryValidator
{
    /// <summary>Returns a complete protected-anchor route or a bounded failure.</summary>
    ProviderRouteValidationResult Validate(
        IReadOnlyList<RouteCoordinate> anchors, ProviderRouteResult providerRoute, CancellationToken cancellationToken);
}

/// <summary>Contains validated budgeted geometry and complete waypoint indices.</summary>
public sealed record ProviderRouteValidationResult(
    bool Succeeded, IReadOnlyList<RouteCoordinate>? Geometry, IReadOnlyList<int>? WaypointIndices, string? ErrorCode)
{
    /// <summary>Creates a bounded validation failure.</summary>
    public static ProviderRouteValidationResult Failure(string code) => new(false, null, null, code);
}
