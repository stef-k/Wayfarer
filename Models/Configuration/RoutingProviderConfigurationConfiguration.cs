using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Defines relational constraints for routing provider configurations.</summary>
public sealed class RoutingProviderConfigurationConfiguration : IEntityTypeConfiguration<RoutingProviderConfiguration>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RoutingProviderConfiguration> builder)
    {
        builder.ToTable("RoutingProviderConfigurations");
        builder.Property(item => item.MinimumIntervalMilliseconds).HasDefaultValue(1000);
        builder.ToTable(table => table.HasCheckConstraint(
            "CK_RoutingProviderConfigurations_MinimumIntervalMilliseconds",
            "\"MinimumIntervalMilliseconds\" >= 0 AND \"MinimumIntervalMilliseconds\" <= 60000"));
        builder.Property(item => item.RowVersion).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.HasMany(item => item.ProfileMappings).WithOne(item => item.RoutingProviderConfiguration)
            .HasForeignKey(item => item.RoutingProviderConfigurationId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Defines the unique provider/transport-profile mapping contract.</summary>
public sealed class RoutingProviderProfileMappingConfiguration : IEntityTypeConfiguration<RoutingProviderProfileMapping>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RoutingProviderProfileMapping> builder)
    {
        builder.ToTable("RoutingProviderProfileMappings");
        builder.HasKey(item => new { item.RoutingProviderConfigurationId, item.TransportProfileId });
        builder.HasOne(item => item.TransportProfile).WithMany().HasForeignKey(item => item.TransportProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Defines singleton settings ownership of the one selected routing provider.</summary>
public sealed class ApplicationSettingsRoutingConfiguration : IEntityTypeConfiguration<ApplicationSettings>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApplicationSettings> builder)
    {
        builder.Property(item => item.RowVersion).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.Property(item => item.ExternalRouteGenerationVersion).HasDefaultValue(1);
        builder.HasOne(item => item.ActiveRoutingProviderConfiguration).WithMany()
            .HasForeignKey(item => item.ActiveRoutingProviderConfigurationId).OnDelete(DeleteBehavior.Restrict);
    }
}
