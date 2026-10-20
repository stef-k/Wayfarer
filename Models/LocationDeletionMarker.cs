using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models;

/// <summary>Lifetime owner/key deletion intent; retains no Location payload or deletion history.</summary>
public sealed class LocationDeletionMarker
{
    /// <summary>The account whose replay is suppressed; complete account erasure cascades this metadata.</summary>
    public required string UserId { get; set; }

    /// <summary>The original portable identity, including a historical singleton zero UUID.</summary>
    public Guid IdempotencyKey { get; set; }
}

/// <summary>The composite primary key also supports owner lookup and account erasure without another index.</summary>
public sealed class LocationDeletionMarkerConfiguration : IEntityTypeConfiguration<LocationDeletionMarker>
{
    /// <summary>Constrains markers to their owning account and original UUID.</summary>
    public void Configure(EntityTypeBuilder<LocationDeletionMarker> builder)
    {
        builder.ToTable("LocationDeletionMarkers");
        builder.HasKey(marker => new { marker.UserId, marker.IdempotencyKey });
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(marker => marker.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
