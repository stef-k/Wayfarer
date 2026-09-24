using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Supplies explicit distinct credential identities for existing provider-domain tests.</summary>
internal static class CredentialTestFactory
{
    /// <summary>Shares the fixture key authority while isolating the companion namespace.</summary>
    internal static PersonalProviderCredentialService Create(IDataProtectionProvider provider) =>
        new(provider, new StableDataProtectionProvider(provider.CreateProtector("test-stable-identity")));
}
