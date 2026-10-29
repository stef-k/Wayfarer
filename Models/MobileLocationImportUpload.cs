using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models;

/// <summary>Account-lifetime request identity and the sole custody of its current immutable file generation.</summary>
public sealed class MobileLocationImportUpload
{
    /// <summary>Internal identity survives account erasure until removal is confirmed.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Nullable solely for account erasure; never infer cleanup authority from missing ownership.</summary>
    public string? UserId { get; set; }
    /// <summary>Client request identity, distinct from observation keys.</summary>
    public Guid? RequestId { get; set; }
    /// <summary>Immutable native format, declared length, digest and explicit enrichment intent.</summary>
    public string? Format { get; set; }
    public long? ByteLength { get; set; }
    public string? Sha256 { get; set; }
    public bool EnrichmentRequested { get; set; }
    /// <summary>Database-clock allocation/acceptance/retirement facts, retained for replay.</summary>
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? RetiredAtUtc { get; set; }
    /// <summary>SET NULL preserves custody and the last result when history is deleted.</summary>
    public int? ImportId { get; set; }
    public LocationImport? Import { get; set; }
    /// <summary>Monotonic current-attempt generation; never reuse a reference.</summary>
    public int Generation { get; set; }
    public string? FileReference { get; set; }
    public string FileState { get; set; } = ManagedImportFileState.Removed;
    public long ReservedBytes { get; set; }
    public long VerifiedBytes { get; set; }
    /// <summary>Admission-time owner byte ceiling, checked transactionally by PostgreSQL.</summary>
    public long OwnerByteLimit { get; set; }
    /// <summary>Exact upload or cleanup lease; no expiry revives an old generation.</summary>
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public DateTime? UploadDeadlineUtc { get; set; }
    /// <summary>Durable irreversible removal obligation, separate from import completion.</summary>
    public string? CleanupReason { get; set; }
    public DateTime? CleanupEligibleAtUtc { get; set; }
    public DateTime? RetryAtUtc { get; set; }
    public string? FailureCode { get; set; }
    /// <summary>Minimal last accepted result survives explicit or age-based history retirement.</summary>
    public string? LastState { get; set; }
    public int LastProcessedRecords { get; set; }
    public int? LastTotalRecords { get; set; }
    public int LastExecutionEpoch { get; set; }
}

/// <summary>File states are separate from the existing import state machine.</summary>
public static class ManagedImportFileState
{
    public const string Uploading = "Uploading";
    public const string Retained = "Retained";
    public const string CleanupClaimed = "CleanupClaimed";
    public const string Removed = "Removed";
}

/// <summary>One database/store binding prevents a second local root from impersonating the managed store.</summary>
public sealed class ManagedImportStore
{
    public int Id { get; set; } = 1;
    public required string Identity { get; set; }
}

/// <summary>Enforces unique request/file/import identities and retained reservation shape.</summary>
public sealed class MobileLocationImportUploadConfiguration : IEntityTypeConfiguration<MobileLocationImportUpload>
{
    public void Configure(EntityTypeBuilder<MobileLocationImportUpload> builder)
    {
        builder.HasKey(row => row.Id);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(row => row.Import).WithMany().HasForeignKey(row => row.ImportId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(row => new { row.UserId, row.RequestId }).IsUnique();
        builder.HasIndex(row => row.ImportId).IsUnique();
        builder.HasIndex(row => row.FileReference).IsUnique();
        builder.HasIndex(row => row.UserId).IsUnique().HasDatabaseName("UX_ManagedUpload_LiveOwner")
            .HasFilter("\"FileState\" = 'Uploading'");
        builder.HasIndex(row => new { row.RetryAtUtc, row.Id }).HasFilter("\"RetryAtUtc\" IS NOT NULL");
        builder.HasIndex(row => new { row.FileState, row.LeaseExpiresAtUtc, row.Id });
        builder.HasIndex(row => new { row.FileState, row.CleanupEligibleAtUtc, row.Id });
        builder.HasIndex(row => new { row.UserId, row.CreatedAtUtc });
        builder.Property(row => row.Format).HasMaxLength(24);
        builder.Property(row => row.Sha256).HasMaxLength(64);
        builder.Property(row => row.FileReference).HasMaxLength(64);
        builder.Property(row => row.FileState).HasMaxLength(20);
        builder.Property(row => row.CleanupReason).HasMaxLength(32);
        builder.Property(row => row.FailureCode).HasMaxLength(64);
        builder.Property(row => row.LastState).HasMaxLength(20);
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_ManagedUpload_State", "\"FileState\" IN ('Uploading','Retained','CleanupClaimed','Removed')");
            table.HasCheckConstraint("CK_ManagedUpload_Reservation", "\"ReservedBytes\" >= 0 AND \"VerifiedBytes\" >= 0 AND \"Generation\" >= 0 AND " +
                "((\"FileState\" = 'Removed' AND \"ReservedBytes\" = 0 AND \"FileReference\" IS NULL) OR " +
                "(\"FileState\" <> 'Removed' AND \"FileReference\" IS NOT NULL AND \"ReservedBytes\" > 0 AND \"FileReference\" ~ '^imports/[0-9a-f]{32}\\.(csv|geojson)$'))");
            table.HasCheckConstraint("CK_ManagedUpload_Owner", "(\"UserId\" IS NOT NULL AND \"RequestId\" IS NOT NULL AND " +
                "\"Format\" IS NOT NULL AND \"ByteLength\" IS NOT NULL AND \"Sha256\" IS NOT NULL AND \"Format\" IN ('csv','wayfarer-geojson') AND \"ByteLength\" > 0 AND \"Sha256\" ~ '^[0-9a-f]{64}$') OR " +
                "(\"UserId\" IS NULL AND \"RequestId\" IS NULL AND \"CleanupReason\" = 'account_deleted')");
        });
    }
}

/// <summary>Maps the single qualified storage identity.</summary>
public sealed class ManagedImportStoreConfiguration : IEntityTypeConfiguration<ManagedImportStore>
{
    public void Configure(EntityTypeBuilder<ManagedImportStore> builder)
    {
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.Identity).HasMaxLength(256);
        builder.ToTable(table => table.HasCheckConstraint("CK_ManagedImportStore_Singleton", "\"Id\" = 1"));
    }
}
