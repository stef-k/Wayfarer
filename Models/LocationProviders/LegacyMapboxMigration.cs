namespace Wayfarer.Models.LocationProviders;

/// <summary>Identifies bounded non-secret legacy migration outcomes.</summary>
public enum LegacyMapboxMigrationState
{ None = 0, Migrated = 1, Conflict = 2, ProtectedCredentialUnavailable = 3, Revoked = 4 }

/// <summary>Represents a bounded credential read result.</summary>
public sealed record PersonalCredentialRead(bool Succeeded, string? Credential)
{
    public static PersonalCredentialRead Unavailable { get; } = new(false, null);
    public override string ToString() => $"PersonalCredentialRead {{ Succeeded = {Succeeded} }}";
}

/// <summary>Represents the non-destructive retirement decision.</summary>
public sealed record LegacyMapboxMigrationDecision(bool RetireLegacy, LegacyMapboxMigrationState State);

/// <summary>Centralizes fail-closed legacy retirement decisions.</summary>
public static class LegacyMapboxMigration
{
    /// <summary>Never retires plaintext unless protected readback is available and unambiguous.</summary>
    public static LegacyMapboxMigrationDecision Decide(PersonalCredentialRead protectedRead, IReadOnlyCollection<string> recognizedLegacyValues)
    {
        if (recognizedLegacyValues.Distinct(StringComparer.Ordinal).Count() > 1)
            return new(false, LegacyMapboxMigrationState.Conflict);
        if (!protectedRead.Succeeded)
            return new(false, LegacyMapboxMigrationState.ProtectedCredentialUnavailable);
        return new(recognizedLegacyValues.Count == 1 && recognizedLegacyValues.Single() == protectedRead.Credential,
            LegacyMapboxMigrationState.Migrated);
    }
}
