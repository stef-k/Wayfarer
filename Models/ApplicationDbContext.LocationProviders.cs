using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Models;

/// <summary>Exposes only the cohesive personal location-provider persistence surface.</summary>
public partial class ApplicationDbContext
{
    /// <summary>Gets personal provider profiles.</summary>
    public DbSet<PersonalLocationProviderProfile> PersonalLocationProviderProfiles { get; set; }
    /// <summary>Gets independent active provider selections.</summary>
    public DbSet<PersonalLocationProviderSelection> PersonalLocationProviderSelections { get; set; }
    /// <summary>Gets Geoapify shared-pool guard rows.</summary>
    public DbSet<GeoapifyUsageGuard> GeoapifyUsageGuards { get; set; }
    /// <summary>Gets rolling Geoapify admissions.</summary>
    public DbSet<GeoapifyUsageAdmission> GeoapifyUsageAdmissions { get; set; }
    /// <summary>Gets independent Mapbox product meters.</summary>
    public DbSet<MapboxProductMeter> MapboxProductMeters { get; set; }
    /// <summary>Gets one retained enrichment workflow per user.</summary>
    public DbSet<LocationEnrichmentWorkflow> LocationEnrichmentWorkflows { get; set; }
    /// <summary>Gets bounded per-location enrichment attempt metadata.</summary>
    public DbSet<LocationEnrichmentAttempt> LocationEnrichmentAttempts { get; set; }
}
