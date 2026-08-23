using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.LocationEnrichment;

/// <summary>Stores bounded retry/defer metadata without copying Location or provider payload data.</summary>
public sealed class LocationEnrichmentAttempt
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public int CredentialGeneration { get; set; }
    public int ConfigurationGeneration { get; set; }
    public LocationEnrichmentOutcome Outcome { get; set; }
    public int AdmittedAttemptCount { get; set; }
    public DateTime LastAttemptAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public LocationEnrichmentWorkflow? Workflow { get; set; }
    public Location? Location { get; set; }
}

/// <summary>Maps compact attempt identity, ownership, retention, and due selection.</summary>
public sealed class LocationEnrichmentAttemptConfiguration : IEntityTypeConfiguration<LocationEnrichmentAttempt>
{
    public void Configure(EntityTypeBuilder<LocationEnrichmentAttempt> builder)
    {
        builder.Property(item => item.UserId).HasMaxLength(450);
        builder.Property(item => item.ProviderKey).HasMaxLength(32);
        builder.Property(item => item.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(item => new { item.UserId, item.LocationId }).IsUnique();
        builder.HasIndex(item => new { item.UserId, item.NextAttemptAtUtc });
        builder.HasOne(item => item.Workflow).WithMany(item => item.Attempts).HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Location).WithMany().HasForeignKey(item => item.LocationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.ToTable(table => table.HasCheckConstraint("CK_LocationEnrichmentAttempt_Count",
            "\"AdmittedAttemptCount\" >= 0 AND \"AdmittedAttemptCount\" <= 3"));
    }
}
