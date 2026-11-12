using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wayfarer.Services;

namespace Wayfarer.Models.Configuration;

/// <summary>Persists the small portable list with value equality and recoverable legacy absence.</summary>
public sealed class TripCreditsConfiguration : IEntityTypeConfiguration<Trip>
{
    /// <summary>Uses the shared strict codec for snapshots, persistence and authored originals.</summary>
    public void Configure(EntityTypeBuilder<Trip> builder)
    {
        builder.Property(t => t.Credits).HasColumnType("jsonb")
            .HasConversion(value => TripCreditCodec.Write(value), json => TripCreditCodec.Read(json))
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<TripCredit>>(
                (a, b) => TripCreditCodec.Write(a!) == TripCreditCodec.Write(b!),
                value => TripCreditCodec.Write(value).GetHashCode(),
                value => TripCreditCodec.Read(TripCreditCodec.Write(value))));
    }
}
