using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Supplies the stable runtime credential owner for provider-domain tests.</summary>
internal static class CredentialTestFactory
{
    /// <summary>Uses the fixture provider as the runtime stable authority.</summary>
    internal static PersonalProviderCredentialService Create(IDataProtectionProvider provider) =>
        new(provider);
}
