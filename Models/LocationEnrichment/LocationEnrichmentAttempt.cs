using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.LocationEnrichment;

/// <summary>Stores bounded retry/defer metadata without copying Location or provider payload data.</summary>
public sealed class LocationEnrichmentAttempt
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public int CredentialGeneration { get; set; }
    public int ConfigurationGeneration { get; set; }
    public int SelectionGeneration { get; set; }
    public LocationEnrichmentOutcome Outcome { get; set; }
    public int AdmittedAttemptCount { get; set; }
    public DateTime LastAttemptAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public LocationEnrichmentWorkflow? Workflow { get; set; }
    public Location? Location { get; set; }

    /// <summary>Returns whether this compact authority permits contact at the supplied database time.</summary>
    public bool IsEligible(string providerKey, int credentialGeneration, int configurationGeneration,
        int selectionGeneration, DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Attempt timestamps must be UTC.");
        if (string.IsNullOrEmpty(ProviderKey)) return true;
        var sameAuthority = ProviderKey == providerKey && CredentialGeneration == credentialGeneration
            && ConfigurationGeneration == configurationGeneration && SelectionGeneration == selectionGeneration;
        if (!sameAuthority) return false;
        if (Outcome is LocationEnrichmentOutcome.InvalidCoordinates or LocationEnrichmentOutcome.NoResult
            or LocationEnrichmentOutcome.AttemptLimit) return false;
        return AdmittedAttemptCount < 3 && (!NextAttemptAtUtc.HasValue || NextAttemptAtUtc <= nowUtc);
    }

    /// <summary>Applies an explicit user override for one currently eligible deferred Location.</summary>
    public void ResetDeferred(string providerKey, int credentialGeneration, int configurationGeneration,
        int selectionGeneration, DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Attempt timestamps must be UTC.");
        ProviderKey = providerKey;
        CredentialGeneration = credentialGeneration;
        ConfigurationGeneration = configurationGeneration;
        SelectionGeneration = selectionGeneration;
        Outcome = LocationEnrichmentOutcome.None;
        AdmittedAttemptCount = 0;
        LastAttemptAtUtc = nowUtc;
        NextAttemptAtUtc = nowUtc;
    }
}

/// <summary>Maps compact attempt identity, ownership, retention, and due selection.</summary>
public sealed class LocationEnrichmentAttemptConfiguration : IEntityTypeConfiguration<LocationEnrichmentAttempt>
{
    public void Configure(EntityTypeBuilder<LocationEnrichmentAttempt> builder)
    {
        builder.Property(item => item.UserId).HasMaxLength(450);
        builder.Property(item => item.ProviderKey).HasMaxLength(32);
        builder.Property(item => item.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(item => new { item.UserId, item.LocationId }).IsUnique();
        builder.HasIndex(item => new { item.UserId, item.NextAttemptAtUtc });
        builder.HasOne(item => item.Workflow).WithMany(item => item.Attempts).HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Location).WithMany().HasForeignKey(item => new { item.UserId, item.LocationId })
            .HasPrincipalKey(item => new { item.UserId, item.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.ToTable(table => table.HasCheckConstraint("CK_LocationEnrichmentAttempt_Count",
            "\"AdmittedAttemptCount\" >= 0 AND \"AdmittedAttemptCount\" <= 3"));
    }
}
