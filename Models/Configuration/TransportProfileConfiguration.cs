using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Configures PostgreSQL constraints and concurrency for transport profiles.</summary>
public sealed class TransportProfileConfiguration : IEntityTypeConfiguration<TransportProfile>
{
    /// <summary>Applies the transport-profile relational contract.</summary>
    public void Configure(EntityTypeBuilder<TransportProfile> profile)
    {
        profile.HasKey(item => item.Id);
        profile.HasIndex(item => item.Key).IsUnique();
        profile.Property(item => item.Key).HasMaxLength(80).IsRequired();
        profile.Property(item => item.Label).HasMaxLength(120).IsRequired();
        profile.Property(item => item.Category).HasMaxLength(80).IsRequired();
        profile.Property(item => item.Description).HasMaxLength(500);
        profile.Property(item => item.RowVersion)
            .HasColumnName("xmin")
            .IsRowVersion()
            .ValueGeneratedOnAddOrUpdate();
        profile.HasMany<Segment>()
            .WithOne(segment => segment.TransportProfile)
            .HasForeignKey(segment => segment.TransportProfileId)
            .OnDelete(DeleteBehavior.Restrict);
        profile.ToTable(table => table.HasCheckConstraint(
            "CK_TransportProfile_NormalizedKey",
            "\"Key\" = lower(trim(\"Key\")) AND length(\"Key\") > 0"));
        profile.ToTable(table => table.HasCheckConstraint(
            "CK_TransportProfile_PlanningSpeedKmh",
            "\"PlanningSpeedKmh\" IS NULL OR (\"PlanningSpeedKmh\" > 0 AND \"PlanningSpeedKmh\" < 1.7976931348623157E+308)"));
    }
}
