namespace Wayfarer.Services.ExternalRouting;

/// <summary>Defines the personal verification commit authority.</summary>
public sealed class PersonalRoutingVerificationService
{
    /// <summary>Gets the required deterministic row-lock order.</summary>
    public static IReadOnlyList<PersonalRoutingVerificationLock> CommitLockOrder { get; } =
        [PersonalRoutingVerificationLock.Provider, PersonalRoutingVerificationLock.UserRoutingConfiguration];
}

/// <summary>Identifies rows locked during the short personal-verification commit phase.</summary>
public enum PersonalRoutingVerificationLock
{
    /// <summary>The provider is locked first.</summary>
    Provider,
    /// <summary>The owning user configuration is locked second.</summary>
    UserRoutingConfiguration
}
