using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Defines ownership, concurrency, and fail-closed relational personal-routing rules.</summary>
public sealed class UserRoutingConfigurationConfiguration : IEntityTypeConfiguration<UserRoutingConfiguration>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserRoutingConfiguration> builder)
    {
        builder.ToTable("UserRoutingConfigurations", table =>
        {
            table.HasCheckConstraint("CK_UserRoutingConfigurations_Version", "\"ConfigurationVersion\" >= 1");
            table.HasCheckConstraint("CK_UserRoutingConfigurations_CredentialConsistency",
                "(\"CredentialPresent\" AND \"CredentialCiphertext\" IS NOT NULL) OR (NOT \"CredentialPresent\" AND \"CredentialCiphertext\" IS NULL)");
            table.HasCheckConstraint("CK_UserRoutingConfigurations_DefaultMode",
                "\"SelectedProviderConfigurationId\" IS NOT NULL OR (NOT \"CredentialPresent\" AND \"CredentialCiphertext\" IS NULL AND \"VerifiedUserConfigurationVersion\" IS NULL AND \"VerifiedProviderConfigurationVersion\" IS NULL AND \"VerificationStatus\" IS NULL)");
            table.HasCheckConstraint("CK_UserRoutingConfigurations_VerifiedPair",
                "(\"VerifiedUserConfigurationVersion\" IS NULL AND \"VerifiedProviderConfigurationVersion\" IS NULL) OR (\"VerifiedUserConfigurationVersion\" IS NOT NULL AND \"VerifiedProviderConfigurationVersion\" IS NOT NULL)");
        });
        builder.HasKey(item => item.UserId);
        builder.Property(item => item.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(item => item.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(item => item.RowVersion).HasColumnName("xmin").IsRowVersion().ValueGeneratedOnAddOrUpdate();
        builder.HasOne(item => item.User).WithOne().HasForeignKey<UserRoutingConfiguration>(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.SelectedProviderConfiguration).WithMany()
            .HasForeignKey(item => item.SelectedProviderConfigurationId).OnDelete(DeleteBehavior.Restrict);
    }
}
