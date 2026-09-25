using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Read-only stable activation inventory; never decrypts legacy rollback evidence.</summary>
public sealed class StableIdentityReadiness(ApplicationDbContext db, PersonalProviderCredentialService credentials)
{
    /// <summary>Classifies durable states using bounded counts without modifying profile authority.</summary>
    public async Task<StableIdentityStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var active = 0;
        var ready = 0;
        var pending = 0;
        var blocked = 0;
        var inactive = 0;
        foreach (var profile in await db.PersonalLocationProviderProfiles.AsNoTracking().ToListAsync(cancellationToken))
        {
            if (profile.RevokedAt != null)
            {
                if (profile.ProtectedCredential != null || profile.StableProtectedCredential != null) blocked++;
                else inactive++;
            }
            else if (profile.ProtectedCredential == null && profile.StableProtectedCredential == null) inactive++;
            else
            {
                active++;
                if (profile.StableProtectedCredential == null) pending++;
                else if (credentials.Read(profile).Succeeded) ready++;
                else blocked++;
            }
        }
        return new(active, ready, pending, blocked, inactive);
    }
}
