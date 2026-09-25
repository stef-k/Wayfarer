using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Owns explicit F1 inventory and all-or-nothing PostgreSQL companion preparation.</summary>
public sealed class StableIdentityPreparation(ApplicationDbContext db, LegacyCredentialPreparationCodec credentials)
{
    /// <summary>Inspects durable state without tracking or modifying any row.</summary>
    public async Task<StableIdentityStatus> StatusAsync(CancellationToken cancellationToken = default) =>
        Inspect(await db.PersonalLocationProviderProfiles.AsNoTracking().ToListAsync(cancellationToken));

    /// <summary>Fills only missing companions under bounded table-wide write exclusion.</summary>
    public async Task<StableIdentityStatus> PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql() || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Preparation requires PostgreSQL and an unchanged context.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // EXCLUSIVE also excludes SELECT FOR UPDATE used by profile mutations.
            // Plain readers remain possible. The operator must still quiesce the service.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "LOCK TABLE \"PersonalLocationProviderProfiles\" IN EXCLUSIVE MODE", cancellationToken);
            var profiles = await db.PersonalLocationProviderProfiles.AsNoTracking().ToListAsync(cancellationToken);
            if (Inspect(profiles).Blocked != 0)
                throw new InvalidOperationException("Credential preparation is blocked.");

            foreach (var profile in profiles.Where(item => item.RevokedAt == null && item.ProtectedCredential != null))
            {
                if (profile.StableProtectedCredential != null) continue;
                credentials.PrepareStable(profile);
                // Attach only this property: preparation cannot stamp or overwrite authority fields.
                db.Attach(profile);
                db.Entry(profile).Property(item => item.StableProtectedCredential).IsModified = true;
            }
            await db.SaveChangesAsync(cancellationToken);
            var verified = await StatusAsync(cancellationToken);
            if (!verified.Ready) throw new InvalidOperationException("Credential preparation verification failed.");
            await transaction.CommitAsync(cancellationToken);
            return verified;
        }
        catch (Exception)
        {
            // Disposal rolls back; discard staged copies so this scope cannot accidentally save them later.
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("Credential preparation failed; no preparation changes were committed.");
        }
    }

    /// <summary>Classifies every row, including inconsistent stable-only and revoked states, without diagnostics containing secrets.</summary>
    private StableIdentityStatus Inspect(IEnumerable<PersonalLocationProviderProfile> profiles)
    {
        var active = 0;
        var ready = 0;
        var pending = 0;
        var blocked = 0;
        var inactive = 0;
        foreach (var profile in profiles)
        {
            if (profile.RevokedAt != null || profile.ProtectedCredential == null)
            {
                if (profile.StableProtectedCredential != null || (profile.RevokedAt != null && profile.ProtectedCredential != null))
                    blocked++;
                else inactive++;
                continue;
            }
            active++;
            var legacy = credentials.ReadLegacy(profile);
            if (!legacy.Succeeded) { blocked++; continue; }
            if (profile.StableProtectedCredential == null) { pending++; continue; }
            var stable = credentials.ReadStable(profile);
            if (stable.Succeeded && string.Equals(legacy.Credential, stable.Credential, StringComparison.Ordinal))
                ready++;
            else blocked++;
        }
        return new(active, ready, pending, blocked, inactive);
    }
}

/// <summary>Contains bounded non-secret migration counts only.</summary>
public sealed record StableIdentityStatus(int Active, int StableReady, int Pending, int Blocked, int Inactive)
{
    /// <summary>Reports whether every durable profile meets the selected preparation or activation contract.</summary>
    public bool Ready => Pending == 0 && Blocked == 0;
}
