using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Same-row history facts, coherence checks and the three model-representable initial indexes.</summary>
public sealed class LocationHistoryConfiguration : IEntityTypeConfiguration<Location>, IEntityTypeConfiguration<HistoryPreparationState>
{
    /// <summary>Geometry-expression GiST is migration-owned; no duplicated geometry column is introduced.</summary>
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.Metadata.Model.GetOrAddPostgresExtension(null, "pg_trgm", null);
        builder.Property(row => row.CaptureLocalTimestamp).HasColumnType("timestamp without time zone");
        builder.Property(row => row.HistorySearchText).UseCollation("C");
        // Status completes capture membership coverage without changing the UTC search keys.
        NpgsqlIndexBuilderExtensions.IncludeProperties(
            builder.HasIndex(row => new { row.UserId, row.LocalTimestamp, row.Id }),
            row => new { row.Coordinates, row.CaptureLocalTimestamp, row.ActivityTypeId, row.HistoryTimeStatus })
            .HasDatabaseName("IX_Location_HistoryUtc");
        builder.HasIndex(row => new { row.UserId, row.CaptureLocalTimestamp, row.Id }).HasDatabaseName("IX_Location_HistoryCapture");
        builder.HasIndex(row => row.HistorySearchText).HasMethod("gin").HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_Location_HistoryText");
        builder.ToTable(table => table.HasCheckConstraint("CK_Location_HistoryTime", """
            ("HistoryTimeVersion" IS NULL AND "HistoryTimeStatus" IS NULL AND "HistoryTimeFailureCode" IS NULL
                AND "CaptureLocalTimestamp" IS NULL AND "CaptureTimeZoneId" IS NULL AND "CaptureOffsetSeconds" IS NULL)
            OR ("HistoryTimeVersion" IS NOT NULL AND "HistoryTimeStatus" IS NOT NULL AND (
                ("HistoryTimeStatus" = 'resolved' AND "HistoryTimeFailureCode" IS NULL AND "CaptureLocalTimestamp" IS NOT NULL
                    AND "CaptureTimeZoneId" IS NOT NULL AND "CaptureOffsetSeconds" IS NOT NULL)
                OR ("HistoryTimeStatus" = 'unresolved' AND "HistoryTimeFailureCode" IS NOT NULL
                    AND "CaptureLocalTimestamp" IS NULL AND "CaptureTimeZoneId" IS NULL AND "CaptureOffsetSeconds" IS NULL)))
            """));
        builder.ToTable(table => table.HasCheckConstraint("CK_Location_HistoryText",
            "(\"HistorySearchText\" IS NULL) = (\"HistoryTextVersion\" IS NULL)"));
    }

    /// <summary>Only the two fixed generation keys are valid.</summary>
    public void Configure(EntityTypeBuilder<HistoryPreparationState> builder)
    {
        builder.HasKey(state => state.Kind);
        builder.ToTable(table => table.HasCheckConstraint("CK_HistoryPreparation_Kind", "\"Kind\" IN ('time', 'text')"));
    }
}
