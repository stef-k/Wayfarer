using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Describes the proposed mode and explicit duration-provenance state.</summary>
/// <param name="Mode">Durable public/interchange mode value.</param>
/// <param name="TransportProfileId">Canonical linked transport-profile identity.</param>
/// <param name="DurationSource">Explicit Automatic or Manual duration ownership.</param>
/// <param name="ManualDurationMinutes">Submitted Manual duration, otherwise ignored.</param>
/// <param name="AllowUnavailableAutomatic">Whether an administrator-owned compatibility operation may clear Automatic duration without speed.</param>
/// <param name="UsePlanningSpeedOverride">Whether a profile mutation supplies the canonical proposed speed.</param>
/// <param name="PlanningSpeedKmhOverride">Proposed speed, including null for a confirmed clear.</param>
public sealed record SegmentMeasurementProposal(
    string Mode,
    Guid? TransportProfileId,
    EstimatedDurationSource DurationSource,
    double? ManualDurationMinutes,
    bool AllowUnavailableAutomatic = false,
    bool UsePlanningSpeedOverride = false,
    double? PlanningSpeedKmhOverride = null);

/// <summary>Measurements sourced from a protected proposal or an unchanged canonical trusted route.</summary>
internal sealed record PreservedRouteMeasurements(double? DistanceKm, TimeSpan? Duration, EstimatedDurationSource Source);

/// <summary>Measurement portion of the transaction-neutral locked Segment aggregate core.</summary>
public static partial class SegmentRouteReconciler
{
    /// <summary>Validates a locked canonical aggregate without changing tracked or database state.</summary>
    internal static async Task<IReadOnlyList<string>> ValidateLockedAggregateAsync(
        ApplicationDbContext dbContext,
        Segment segment,
        CancellationToken cancellationToken)
    {
        var proposal = new SegmentRouteProposal(
            segment.Id,
            segment.FromPlaceId,
            segment.ToPlaceId,
            segment.Waypoints.OrderBy(item => item.Position)
                .Select(item => new SegmentWaypointProposal(item.PlaceId, item.Position, item.RouteVertexIndex))
                .ToArray(),
            segment.RouteGeometry);
        var placesById = await LoadProposalPlacesAsync(dbContext, proposal, cancellationToken);
        return Validate(segment.TripId, proposal, placesById, CopyGeometry(proposal));
    }

    /// <summary>Recalculates measurements on a tracked zero-waypoint Segment inside a caller-owned mutation.</summary>
    internal static async Task ReconcileTrackedMeasurementsAsync(
        ApplicationDbContext dbContext,
        Segment segment,
        CancellationToken cancellationToken)
    {
        var proposal = new SegmentRouteProposal(
            segment.Id, segment.FromPlaceId, segment.ToPlaceId, [], segment.RouteGeometry);
        var anchors = new[] { segment.FromPlace, segment.ToPlace }.Where(place => place != null).Cast<Place>().ToArray();
        var errors = new List<string>();
        var measurements = await CalculateMeasurementsAsync(
            dbContext, segment, proposal, segment.RouteGeometry, anchors, errors, cancellationToken);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        ApplyMeasurements(segment, measurements!);
    }

    /// <summary>
    /// Reconciles one complete canonical aggregate after the caller has acquired profile and Segment locks.
    /// This core neither starts nor commits a transaction and does not call SaveChanges.
    /// </summary>
    internal static async Task<SegmentRouteReconciliationResult> ReconcileLockedAsync(
        ApplicationDbContext dbContext,
        SegmentRouteProposal proposal,
        bool refreshCanonicalState,
        CancellationToken cancellationToken)
    {
        if (refreshCanonicalState)
        {
            var refreshScope = await LoadCanonicalRefreshScopeAsync(dbContext, proposal, cancellationToken);
            if (refreshScope == null) return new(false, ["Segment was not found."], []);
            RefreshStaleCanonicalState(dbContext, refreshScope);
        }

        var segment = await LoadAggregateAsync(dbContext, proposal.SegmentId, cancellationToken);
        if (segment == null) return new(false, ["Segment was not found."], []);
        var placesById = await LoadProposalPlacesAsync(dbContext, proposal, cancellationToken);
        var geometry = CopyGeometry(proposal);
        var errors = Validate(segment.TripId, proposal, placesById, geometry);
        var anchors = BuildAnchorChain(proposal, placesById);
        var measurement = await CalculateMeasurementsAsync(
            dbContext, segment, proposal, geometry, anchors, errors, cancellationToken);
        if (errors.Count > 0) return new(false, errors, anchors);

        if (!SameRouteIdentity(segment, proposal)) ClearRouteMetadata(segment);
        if (dbContext.Database.IsRelational())
        {
            await dbContext.Set<SegmentWaypoint>()
                .Where(item => item.SegmentId == segment.Id)
                .ExecuteDeleteAsync(cancellationToken);
            DetachCurrentWaypoints(dbContext, segment);
        }
        ApplyTrackedState(dbContext, segment, proposal, placesById, geometry);
        ApplyMeasurements(segment, measurement!);
        if (proposal.ApplyNotes) segment.Notes = proposal.NotesHtml ?? string.Empty;
        return new(true, [], anchors);
    }

    private static async Task<CalculatedSegmentMeasurements?> CalculateMeasurementsAsync(
        ApplicationDbContext dbContext,
        Segment segment,
        SegmentRouteProposal routeProposal,
        LineString? geometry,
        IReadOnlyList<Place> anchors,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var proposal = routeProposal.Measurement ?? new(
            segment.Mode,
            segment.TransportProfileId,
            segment.EstimatedDurationSource,
            segment.EstimatedDuration?.TotalMinutes,
            AllowUnavailableAutomatic: true);
        if (!Enum.IsDefined(proposal.DurationSource))
        {
            errors.Add("Duration source must be Automatic or Manual.");
            return null;
        }

        TransportProfile? profile = null;
        if (proposal.TransportProfileId.HasValue)
            profile = await dbContext.Set<TransportProfile>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == proposal.TransportProfileId.Value, cancellationToken);
        if (proposal.TransportProfileId.HasValue && profile == null)
            errors.Add("Transport profile was not found.");
        else if (profile != null && !string.Equals(profile.Key, TransportProfile.NormalizeKey(proposal.Mode), StringComparison.Ordinal))
            errors.Add("Mode must match the linked transport profile.");

        if (routeProposal.PreservedMeasurements is { } preserved)
            return errors.Count == 0
                ? new(proposal.Mode, proposal.TransportProfileId, preserved.Source, preserved.DistanceKm, preserved.Duration)
                : null;

        SegmentDistanceMeasurement? distance = null;
        try
        {
            var coordinates = EffectiveCoordinates(routeProposal, geometry, anchors);
            if (coordinates != null)
                distance = SegmentMeasurementCalculator.CalculateDistance(coordinates);
        }
        catch (ArgumentException exception)
        {
            errors.Add(exception.Message);
        }

        TimeSpan? duration = null;
        try
        {
            if (proposal.DurationSource == EstimatedDurationSource.Manual)
            {
                if (!proposal.ManualDurationMinutes.HasValue)
                    errors.Add("Manual duration is required.");
                else
                    duration = SegmentMeasurementCalculator.NormalizeManualDuration(proposal.ManualDurationMinutes.Value);
            }
            else if ((proposal.UsePlanningSpeedOverride ? proposal.PlanningSpeedKmhOverride : profile?.PlanningSpeedKmh)
                     is > 0d and var speed && double.IsFinite(speed))
            {
                duration = distance.HasValue
                    ? SegmentMeasurementCalculator.CalculateAutomaticDuration(distance.Value.UnroundedMetres, speed)
                    : null;
            }
            else if (!proposal.AllowUnavailableAutomatic)
            {
                errors.Add("Automatic duration requires a linked profile with a positive planning speed.");
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            errors.Add(exception.Message);
        }

        return errors.Count == 0
            ? new(proposal.Mode, proposal.TransportProfileId, proposal.DurationSource, distance?.RoundedKilometres, duration)
            : null;
    }

    /// <summary>Uses exact geometry and ordered anchor/index semantics to identify a retained route.</summary>
    internal static bool SameRouteIdentity(Segment segment, SegmentRouteProposal proposal) =>
        segment.RouteGeometry != null && proposal.RouteGeometry != null
        && segment.RouteGeometry.EqualsExact(proposal.RouteGeometry)
        && segment.FromPlaceId == proposal.FromPlaceId && segment.ToPlaceId == proposal.ToPlaceId
        && segment.Waypoints.OrderBy(item => item.Position)
            .Select(item => new SegmentWaypointProposal(item.PlaceId, item.Position, item.RouteVertexIndex))
            .SequenceEqual(proposal.Waypoints);

    /// <summary>Removes attribution and instructions that no longer describe the submitted route.</summary>
    private static void ClearRouteMetadata(Segment segment)
    {
        segment.RouteInstructionsJson = null;
        segment.RouteProvider = null;
        segment.RouteProviderConfigurationId = null;
        segment.RouteProviderConfigurationVersion = null;
        segment.RouteTransportProfileId = null;
        segment.RouteMappingMode = null;
        segment.RouteGeneratedAt = null;
        segment.RouteAttribution = null;
        segment.RouteStorageMode = null;
    }

    private static IReadOnlyList<Coordinate>? EffectiveCoordinates(
        SegmentRouteProposal proposal,
        LineString? geometry,
        IReadOnlyList<Place> anchors)
    {
        if (geometry != null)
            return geometry.Coordinates;
        if (!proposal.FromPlaceId.HasValue || !proposal.ToPlaceId.HasValue
            || anchors.Count != proposal.Waypoints.Count + 2
            || anchors.Any(place => !HasValidLocation(place)))
            return null;
        return anchors.Select(place => place.Location!.Coordinate).ToArray();
    }

    private static void ApplyMeasurements(Segment segment, CalculatedSegmentMeasurements measurements)
    {
        segment.Mode = measurements.Mode;
        segment.TransportProfileId = measurements.TransportProfileId;
        segment.EstimatedDurationSource = measurements.DurationSource;
        segment.EstimatedDistanceKm = measurements.DistanceKm;
        segment.EstimatedDuration = measurements.Duration;
    }

    private sealed record CalculatedSegmentMeasurements(
        string Mode,
        Guid? TransportProfileId,
        EstimatedDurationSource DurationSource,
        double? DistanceKm,
        TimeSpan? Duration);
}
