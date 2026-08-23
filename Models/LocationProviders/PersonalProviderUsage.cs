using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models.LocationProviders;

/// <summary>Stores the stable Geoapify shared rolling-credit guard.</summary>
public sealed class GeoapifyUsageGuard
{
    [StringLength(450)] public string UserId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int CreditLimit { get; set; } = 2500;
    public uint RowVersion { get; private set; }
}

/// <summary>Records one admitted Geoapify contact without personal request content.</summary>
public sealed class GeoapifyUsageAdmission
{
    public long Id { get; set; }
    [StringLength(450)] public string UserId { get; set; } = string.Empty;
    public int Credits { get; set; }
    public PersonalProviderProduct Product { get; set; }
    public DateTimeOffset AdmittedAt { get; set; }
}

/// <summary>Stores one durable Mapbox product safety-cycle counter.</summary>
public sealed class MapboxProductMeter
{
    [StringLength(450)] public string UserId { get; set; } = string.Empty;
    public PersonalProviderProduct Product { get; set; }
    public bool Enabled { get; set; } = true;
    public int Limit { get; set; } = 1000;
    public DateOnly CycleStart { get; set; }
    public int AdmittedCount { get; set; }
    public uint RowVersion { get; private set; }
}
