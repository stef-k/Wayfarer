using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Util;

/// <summary>Limits installation Manager administration to persisted accounts with exactly the User role.</summary>
internal static class ManagerTargetUsers
{
    /// <summary>
    /// Selects User-only targets, excluding privileged, unknown and roleless accounts regardless of name,
    /// activity, protection or Group membership. Shared by listing and every existing-target action.
    /// </summary>
    internal static IQueryable<ApplicationUser> Eligible(ApplicationDbContext db) =>
        db.Users.Where(user =>
            db.UserRoles.Count(membership => membership.UserId == user.Id) == 1 &&
            db.UserRoles.Any(membership => membership.UserId == user.Id &&
                db.Roles.Any(role => role.Id == membership.RoleId && role.Name == ApplicationRoles.User)));

    /// <summary>Checks the complete persisted installation role set before disclosing or mutating a resolved target.</summary>
    internal static Task<bool> IsEligibleAsync(ApplicationDbContext db, string id) =>
        Eligible(db).AnyAsync(user => user.Id == id);
}
