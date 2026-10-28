using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

/// <summary>Preserves Identity deletion authority while ordering it with upload publication and custody erasure.</summary>
public static class ManagedImportAccountDeletion
{
    /// <summary>The database erasure trigger runs in this same deletion transaction and retains only pending file evidence.</summary>
    public static async Task<IdentityResult> DeleteAsync(ApplicationDbContext db, UserManager<ApplicationUser> users,
        ApplicationUser user, CancellationToken token = default)
    {
        if (!db.Database.IsNpgsql()) return await users.DeleteAsync(user);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, user.Id, token);
        var result = await users.DeleteAsync(user);
        if (result.Succeeded) await transaction.CommitAsync(token);
        return result;
    }
}
