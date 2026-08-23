using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Models.Configuration;

/// <summary>Prevents generic token/authentication readers from exposing legacy Mapbox recovery plaintext.</summary>
public sealed class LegacyProviderTokenReadConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> builder) =>
        builder.HasQueryFilter(item => item.Name.Trim().ToLower() != "mapbox");
}

/// <summary>Defines bounded relational authority for personal provider profiles and selections.</summary>
public sealed class PersonalLocationProviderConfiguration : IEntityTypeConfiguration<PersonalLocationProviderProfile>
{
    public void Configure(EntityTypeBuilder<PersonalLocationProviderProfile> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasAlternateKey(item => new { item.UserId, item.ProviderKey });
        builder.HasIndex(item => new { item.UserId, item.ProviderKey }).IsUnique();
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(item => item.RowVersion).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_PersonalProvider_Provider", "\"ProviderKey\" IN ('geoapify', 'mapbox')");
            table.HasCheckConstraint("CK_PersonalProvider_Generations", "\"CredentialGeneration\" > 0 AND \"GeocodingGeneration\" > 0 AND \"RoutingGeneration\" > 0");
            table.HasCheckConstraint("CK_PersonalProvider_Verification", "\"GeocodingVerification\" BETWEEN 0 AND 3 AND \"RoutingVerification\" BETWEEN 0 AND 3");
        });
    }
}

/// <summary>Defines independent provider selection integrity.</summary>
public sealed class PersonalLocationProviderSelectionConfiguration : IEntityTypeConfiguration<PersonalLocationProviderSelection>
{
    public void Configure(EntityTypeBuilder<PersonalLocationProviderSelection> builder)
    {
        builder.HasKey(item => item.UserId);
        builder.HasOne<ApplicationUser>().WithOne().HasForeignKey<PersonalLocationProviderSelection>(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PersonalLocationProviderProfile>().WithMany()
            .HasForeignKey(item => new { item.UserId, item.GeocodingProviderKey })
            .HasPrincipalKey(item => new { item.UserId, item.ProviderKey }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PersonalLocationProviderProfile>().WithMany()
            .HasForeignKey(item => new { item.UserId, item.RoutingProviderKey })
            .HasPrincipalKey(item => new { item.UserId, item.ProviderKey }).OnDelete(DeleteBehavior.Restrict);
        builder.Property(item => item.RowVersion).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_PersonalProviderSelection_Geocoding", "\"GeocodingProviderKey\" IS NULL OR \"GeocodingProviderKey\" IN ('geoapify', 'mapbox')");
            table.HasCheckConstraint("CK_PersonalProviderSelection_Routing", "\"RoutingProviderKey\" IS NULL OR \"RoutingProviderKey\" IN ('geoapify', 'mapbox')");
        });
    }
}
