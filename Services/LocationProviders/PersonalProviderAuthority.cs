using System.Text.Json.Serialization;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Identifies bounded admission outcomes safe for diagnostics.</summary>
public enum PersonalProviderAdmissionCategory
{ Admitted, InvalidCost, NoProviderSelected, UnsupportedProvider, UnsupportedProduct, Unauthorized, Unverified, ConsentRequired, CredentialUnavailable, Exhausted }

/// <summary>Contains server-internal immutable contact authority; it must never be serialized.</summary>
public sealed class PersonalProviderAuthoritySnapshot
{
    public PersonalProviderAuthoritySnapshot(string userId, string providerKey, PersonalProviderCapability capability,
        string credential, int credentialGeneration, int capabilityGeneration, int selectionGeneration,
        int? consentVersion = null, DateTimeOffset? consentedAt = null, int? consentCredentialGeneration = null,
        Guid? profileId = null, PersonalProviderVerification verification = PersonalProviderVerification.Unverified,
        int? verifiedCredentialGeneration = null, int? verifiedCapabilityGeneration = null)
    {
        UserId = userId; ProviderKey = providerKey; Capability = capability; Credential = credential;
        CredentialGeneration = credentialGeneration; CapabilityGeneration = capabilityGeneration;
        SelectionGeneration = selectionGeneration; ConsentVersion = consentVersion; ConsentedAt = consentedAt;
        ConsentCredentialGeneration = consentCredentialGeneration; ProfileId = profileId; Verification = verification;
        VerifiedCredentialGeneration = verifiedCredentialGeneration; VerifiedCapabilityGeneration = verifiedCapabilityGeneration;
    }

    public string UserId { get; }
    public string ProviderKey { get; }
    public PersonalProviderCapability Capability { get; }
    [JsonIgnore] public string Credential { get; }
    public int CredentialGeneration { get; }
    public int CapabilityGeneration { get; }
    public int SelectionGeneration { get; }
    public int? ConsentVersion { get; }
    public DateTimeOffset? ConsentedAt { get; }
    public int? ConsentCredentialGeneration { get; }
    public Guid? ProfileId { get; }
    public PersonalProviderVerification Verification { get; }
    public int? VerifiedCredentialGeneration { get; }
    public int? VerifiedCapabilityGeneration { get; }
    public override string ToString() => $"PersonalProviderAuthoritySnapshot {{ ProviderKey = {ProviderKey}, Capability = {Capability}, CredentialGeneration = {CredentialGeneration}, CapabilityGeneration = {CapabilityGeneration}, SelectionGeneration = {SelectionGeneration} }}";
}

/// <summary>Contains only bounded usage status.</summary>
public sealed record PersonalProviderUsageStatus(int Used, int Limit, string Unit,
    DateTimeOffset? RollingCutoff, DateOnly? CycleStart);

/// <summary>Contains bounded current authority and usage facts without credential material.</summary>
public sealed record PersonalProviderInspection(PersonalProviderAdmissionCategory Category, string? ProviderKey,
    bool GuardEnabled, bool Exhausted, DateTimeOffset? NextAvailableAt, PersonalProviderUsageStatus? Usage,
    PersonalProviderAuthorityBinding? Binding, DateTime DatabaseNowUtc = default)
{
    public bool Available => Category == PersonalProviderAdmissionCategory.Admitted && !Exhausted;
}

/// <summary>Identifies current durable authority for relational attempt classification.</summary>
public sealed record PersonalProviderAuthorityBinding(string ProviderKey, Guid? ProfileId,
    int CredentialGeneration, int CapabilityGeneration, int SelectionGeneration,
    PersonalProviderVerification Verification, int? VerifiedCredentialGeneration,
    int? VerifiedCapabilityGeneration, int? ConsentVersion, DateTimeOffset? ConsentedAt,
    int? ConsentCredentialGeneration);

/// <summary>Returns bounded rejection or admitted server authority.</summary>
public sealed record PersonalProviderAdmission(PersonalProviderAdmissionCategory Category,
    PersonalProviderAuthoritySnapshot? Authority, PersonalProviderUsageStatus? Usage)
{
    public bool Succeeded => Category == PersonalProviderAdmissionCategory.Admitted;
    public static PersonalProviderAdmission Rejected(PersonalProviderAdmissionCategory category,
        PersonalProviderUsageStatus? usage = null) => new(category, null, usage);
}
