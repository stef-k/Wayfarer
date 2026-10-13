using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Database-owned Activity identity and deletion without optional-FK nulling.</summary>
public sealed class ActivityTypeConfiguration : IEntityTypeConfiguration<ActivityType>
{
    /// <summary>Maps the generated key privately; catalog DTOs retain their existing shape.</summary>
    public void Configure(EntityTypeBuilder<ActivityType> builder)
    {
        builder.Property<string>("NormalizedName").IsRequired().UseCollation("C")
            .HasComputedColumnSql("public.wayfarer_activity_name_v1(\"Name\", true)", stored: true);
        builder.HasIndex("NormalizedName").IsUnique().HasDatabaseName("IX_ActivityTypes_NormalizedName");
        builder.ToTable("ActivityTypes", table => table.HasCheckConstraint("CK_ActivityTypes_CanonicalName",
            "(\"Name\" COLLATE \"C\" = public.wayfarer_activity_name_v1(\"Name\", false) COLLATE \"C\" AND \"Name\" <> '') IS TRUE"));
        builder.HasMany<Location>().WithOne(l => l.ActivityType).HasForeignKey(l => l.ActivityTypeId)
            .OnDelete(DeleteBehavior.ClientNoAction);
    }
}

/// <summary>Permanent numeric redirects also protect survivors without current Locations.</summary>
public sealed class ActivityTypeIdRedirectConfiguration : IEntityTypeConfiguration<ActivityTypeIdRedirect>
{
    /// <summary>Maps only the legacy ID and direct restrictive survivor reference.</summary>
    public void Configure(EntityTypeBuilder<ActivityTypeIdRedirect> builder)
    {
        builder.ToTable("ActivityTypeIdRedirects", table => table.HasCheckConstraint("CK_ActivityTypeIdRedirects_Ids",
            "\"LegacyId\" > 0 AND \"ActivityTypeId\" > 0 AND \"LegacyId\" <> \"ActivityTypeId\""));
        builder.HasKey(r => r.LegacyId);
        builder.Property(r => r.LegacyId).ValueGeneratedNever();
        builder.HasOne<ActivityType>().WithMany().HasForeignKey(r => r.ActivityTypeId)
            .OnDelete(DeleteBehavior.ClientNoAction);
    }
}
