using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models;

/// <summary>Default-off photo consent and indexed current references for batched cleanup authority.</summary>
public sealed class ProfilePhotoConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.ShowProfilePhotoOnPublicTrips).HasDefaultValue(false);
        builder.HasIndex(u => u.ProfilePhotoVersion);
    }
}
