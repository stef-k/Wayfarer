using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>Issues and validates opaque confirmation tokens for Place and Region lifecycle deletion.</summary>
public sealed class LifecycleDependencyConfirmation
{
    private const string ContractVersion = "LIFE-1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector _protector;
    private readonly IDataProtector _regionDeletionProtector;
    private readonly IDataProtector _coordinateProtector;
    private readonly IDataProtector _moveProtector;
    private readonly IDataProtector _orderProtector;
    private readonly TimeProvider _timeProvider;
    /// <summary>Uses the application protector for fresh affected Segment aggregate tokens.</summary>
    internal SegmentAggregateTokenService RegionDeletionAggregateTokens { get; }

    /// <summary>Initializes lifecycle confirmation with application data protection and time authority.</summary>
    public LifecycleDependencyConfirmation(IDataProtectionProvider provider, TimeProvider? timeProvider = null)
    {
        _protector = provider.CreateProtector("Wayfarer.PlaceRegionLifecycle.DependencyConfirmation.v1");
        _regionDeletionProtector = provider.CreateProtector("Wayfarer.RegionDeletion.Content.v1");
        _coordinateProtector = provider.CreateProtector("Wayfarer.PlaceCoordinates.Conditions.v1");
        _moveProtector = provider.CreateProtector("Wayfarer.PlaceMove.Conditions.v1");
        _orderProtector = provider.CreateProtector("Wayfarer.PlaceOrder.Conditions.v1");
        _timeProvider = timeProvider ?? TimeProvider.System;
        RegionDeletionAggregateTokens = new(provider);
    }

    /// <summary>Issues transient material conditions, bound to owner/server keys, Trip, typed target and exact intent.</summary>
    internal string IssuePlaceStructure(string operation, string userId, Guid tripId, Guid targetId, string intent, string fingerprint) =>
        PlaceProtector(operation).Protect(JsonSerializer.Serialize(new PlaceStructurePayload(1, operation, userId, tripId, targetId, intent, fingerprint)));

    /// <summary>Checks original material conditions without expiry-based rebasing, private disclosure or receipt semantics.</summary>
    internal bool MatchesPlaceStructure(string? token, string operation, string userId, Guid tripId, Guid targetId, string intent, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var value = JsonSerializer.Deserialize<PlaceStructurePayload>(PlaceProtector(operation).Unprotect(token));
            return value == new PlaceStructurePayload(1, operation, userId, tripId, targetId, intent, fingerprint);
        }
        catch (Exception error) when (error is CryptographicException or JsonException) { return false; }
    }

    /// <summary>Combined Web intent has its own fixed operation tag and complete intent/material hash within coordinate protection.</summary>
    private IDataProtector PlaceProtector(string operation) => operation switch
    {
        "coordinates" or "web-update" => _coordinateProtector,
        "move" => _moveProtector,
        "order" => _orderProtector,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private sealed record PlaceStructurePayload(int Version, string Operation, string UserId, Guid TripId, Guid TargetId, string Intent, string Fingerprint);

    /// <summary>Creates a bounded warning and token bound to the exact dependency identities.</summary>
    public EditorLifecycleConflictDto Create(
        string code,
        string operation,
        string userId,
        Guid tripId,
        Guid targetId,
        LifecycleDependencies dependencies)
    {
        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt.Add(Lifetime);
        var payload = new ConfirmationPayload(
            ContractVersion, userId, tripId, operation, targetId, dependencies.Fingerprint(), issuedAt, expiresAt);
        var token = _protector.Protect(JsonSerializer.Serialize(payload));
        return new(code, operation, targetId,
            Sample(dependencies.EndpointSegmentIds),
            Sample(dependencies.WaypointOnlySegmentIds),
            SampleAssociations(dependencies.WaypointAssociationIds),
            Sample(dependencies.PlaceIds),
            Sample(dependencies.AreaIds),
            token,
            expiresAt);
    }

    /// <summary>Validates identity, scope, expiry, and exact dependency fingerprint without exposing token contents.</summary>
    public bool IsValid(
        string? token,
        string operation,
        string userId,
        Guid tripId,
        Guid targetId,
        LifecycleDependencies dependencies)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<ConfirmationPayload>(_protector.Unprotect(token));
            return payload != null
                && payload.Version == ContractVersion
                && payload.UserId == userId
                && payload.TripId == tripId
                && payload.Operation == operation
                && payload.TargetId == targetId
                && payload.Fingerprint == dependencies.Fingerprint()
                && payload.IssuedAt <= _timeProvider.GetUtcNow()
                && payload.ExpiresAt > _timeProvider.GetUtcNow();
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Protects complete content approval under a purpose that legacy DELETE cannot consume.</summary>
    internal RegionDeletionConfirmationDto IssueRegionDeletion(string userId, Guid tripId, Guid sourceId,
        string places, string areas, RegionDeletionDestinationDto destination, string fingerprint)
    {
        var expires = _timeProvider.GetUtcNow().Add(Lifetime);
        var payload = new RegionDeletionApproval(userId, tripId, sourceId, places, areas, destination,
            fingerprint, expires);
        return new(_regionDeletionProtector.Protect(JsonSerializer.Serialize(payload)), expires);
    }

    /// <summary>Authenticates the token separately from expiry/content checks so malformed input is a 400.</summary>
    internal RegionDeletionApproval ReadRegionDeletion(string token)
    {
        try
        {
            return JsonSerializer.Deserialize<RegionDeletionApproval>(_regionDeletionProtector.Unprotect(token))
                ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw new RegionDeletionRejectedException(400, "region-deletion-request-invalid", "Confirmation is invalid. Request a new preview.");
        }
    }

    /// <summary>Checks time with the same authority used to issue the confirmation.</summary>
    internal bool IsRegionDeletionExpired(RegionDeletionApproval approval) => approval.ExpiresAt <= _timeProvider.GetUtcNow();

    private static EditorLifecycleDependencySampleDto Sample(IEnumerable<Guid> identities)
    {
        var ordered = identities.Distinct().Order().ToArray();
        return new(ordered.Length, ordered.Take(5).ToArray(), ordered.Length > 5);
    }

    private static EditorLifecycleAssociationSampleDto SampleAssociations(
        IEnumerable<(Guid SegmentId, Guid PlaceId)> identities)
    {
        var ordered = identities.Distinct()
            .OrderBy(item => item.SegmentId)
            .ThenBy(item => item.PlaceId)
            .Select(item => new EditorLifecycleWaypointAssociationDto(item.SegmentId, item.PlaceId))
            .ToArray();
        return new(ordered.Length, ordered.Take(5).ToArray(), ordered.Length > 5);
    }

    private sealed record ConfirmationPayload(
        string Version,
        string UserId,
        Guid TripId,
        string Operation,
        Guid TargetId,
        string Fingerprint,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);
}

/// <summary>Canonical identity set used for lifecycle warning, confirmation, and drift comparison.</summary>
public sealed record LifecycleDependencies(
    IReadOnlyList<Guid> EndpointSegmentIds,
    IReadOnlyList<Guid> WaypointOnlySegmentIds,
    IReadOnlyList<(Guid SegmentId, Guid PlaceId)> WaypointAssociationIds,
    IReadOnlyList<Guid> PlaceIds,
    IReadOnlyList<Guid> AreaIds)
{
    /// <summary>Returns whether deleting this target requires explicit confirmation.</summary>
    public bool RequiresConfirmation => EndpointSegmentIds.Count > 0
        || WaypointOnlySegmentIds.Count > 0
        || PlaceIds.Count > 0
        || AreaIds.Count > 0;

    /// <summary>Hashes sorted exact identities without embedding dependency content in the client token.</summary>
    public string Fingerprint()
    {
        var value = string.Join('|',
            Ordered("endpoint", EndpointSegmentIds),
            Ordered("waypoint", WaypointOnlySegmentIds),
            string.Join(',', WaypointAssociationIds.OrderBy(item => item.SegmentId).ThenBy(item => item.PlaceId)
                .Select(item => $"association:{item.SegmentId:N}:{item.PlaceId:N}")),
            Ordered("place", PlaceIds),
            Ordered("area", AreaIds));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Ordered(string category, IEnumerable<Guid> ids) =>
        string.Join(',', ids.Distinct().Order().Select(id => $"{category}:{id:N}"));
}

/// <summary>Protected operation/version-scoped approval; only a hash of authored content leaves the server.</summary>
internal sealed record RegionDeletionApproval(string UserId, Guid TripId, Guid SourceId, string Places, string Areas,
    RegionDeletionDestinationDto Destination, string Fingerprint, DateTimeOffset ExpiresAt);
