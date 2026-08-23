namespace Wayfarer.Models.Dtos;

/// <summary>Represents one travel segment in the backward-compatible public Trip API.</summary>
public class ApiTripSegmentDto
{
    /// <summary>Gets or sets the Segment identity.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the durable transport mode key.</summary>
    public string Mode { get; set; } = "";
    /// <summary>Gets or sets the estimated distance in kilometers.</summary>
    public double? EstimatedDistanceKm { get; set; }
    /// <summary>Gets or sets the estimated duration in minutes.</summary>
    public double? EstimatedDurationMinutes { get; set; }
    /// <summary>Gets or sets the Segment notes.</summary>
    public string? Notes { get; set; }
    /// <summary>Gets or sets the public display order.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Gets or sets the optional starting Place identity.</summary>
    public Guid? FromPlaceId { get; set; }
    /// <summary>Gets or sets the optional destination Place identity.</summary>
    public Guid? ToPlaceId { get; set; }

    /// <summary>Gets or sets the nullable GeoJSON LineString encoded as a JSON string.</summary>
    public string? RouteJson { get; set; }

    /// <summary>Gets the ordered public waypoint identities.</summary>
    public IReadOnlyList<ApiTripSegmentWaypointDto> Waypoints { get; init; } = [];

    /// <summary>Gets whether <see cref="RouteJson"/> contains validated persisted custom geometry.</summary>
    public bool HasCustomRoute { get; init; }

    /// <summary>Gets normalized retained route instructions as bounded JSON.</summary>
    public string? RouteInstructionsJson { get; init; }
    /// <summary>Gets safe retained-route provider identity.</summary>
    public string? RouteProvider { get; init; }
    /// <summary>Gets safe provider configuration identity.</summary>
    public Guid? RouteProviderConfigurationId { get; init; }
    /// <summary>Gets provider configuration and mapping version.</summary>
    public int? RouteProviderConfigurationVersion { get; init; }
    /// <summary>Gets stable transport profile identity used to generate the route.</summary>
    public Guid? RouteTransportProfileId { get; init; }
    /// <summary>Gets generation time for retained/offline presentation.</summary>
    public DateTimeOffset? RouteGeneratedAt { get; init; }
    /// <summary>Gets linked route attribution display contract.</summary>
    public string? RouteAttribution { get; init; }
    /// <summary>Gets provider-authorized offline storage mode.</summary>
    public string? RouteStorageMode { get; init; }
}
