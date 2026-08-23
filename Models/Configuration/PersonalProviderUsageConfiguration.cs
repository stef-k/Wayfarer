using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Models.Configuration;

/// <summary>Defines the stable per-user Geoapify guard lock row.</summary>
public sealed class GeoapifyUsageGuardConfiguration : IEntityTypeConfiguration<GeoapifyUsageGuard>
{
    public void Configure(EntityTypeBuilder<GeoapifyUsageGuard> builder)
    {
        builder.HasKey(item => item.UserId);
        builder.HasOne<ApplicationUser>().WithOne().HasForeignKey<GeoapifyUsageGuard>(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(item => item.RowVersion).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.ToTable(table => table.HasCheckConstraint("CK_GeoapifyUsageGuard_Limit", "\"CreditLimit\" >= 0"));
    }
}

/// <summary>Defines exact rolling-window admissions and their authority index.</summary>
public sealed class GeoapifyUsageAdmissionConfiguration : IEntityTypeConfiguration<GeoapifyUsageAdmission>
{
    public void Configure(EntityTypeBuilder<GeoapifyUsageAdmission> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.AdmittedAt).HasDefaultValueSql("clock_timestamp()").ValueGeneratedOnAdd();
        builder.HasIndex(item => new { item.UserId, item.AdmittedAt });
        builder.HasOne<GeoapifyUsageGuard>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_GeoapifyUsageAdmission_Credits", "\"Credits\" > 0");
            table.HasCheckConstraint("CK_GeoapifyUsageAdmission_Product", "\"Product\" IN (1, 2)");
        });
    }
}

/// <summary>Defines independent durable Mapbox product meters.</summary>
public sealed class MapboxProductMeterConfiguration : IEntityTypeConfiguration<MapboxProductMeter>
{
    public void Configure(EntityTypeBuilder<MapboxProductMeter> builder)
    {
        builder.HasKey(item => new { item.UserId, item.Product });
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(item => item.RowVersion).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_MapboxProductMeter_Product", "\"Product\" IN (3, 4)");
            table.HasCheckConstraint("CK_MapboxProductMeter_Counts", "\"Limit\" >= 0 AND \"AdmittedCount\" >= 0");
        });
    }
}
