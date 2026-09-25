using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Checks local database compatibility and secure bootstrap without mutating either.</summary>
internal static class ApplicationReadiness
{
    /// <summary>Bounds the complete readiness probe and returns only a non-secret boolean result.</summary>
    internal static async Task<bool> IsReadyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.SetCommandTimeout(3);
            await ValidateSchemaAsync(db, timeout.Token);
            if (!await db.ApplicationSettings.AnyAsync(timeout.Token) || !await db.ActivityTypes.AnyAsync(timeout.Token))
                return false;
            var admins = await (from user in db.Users.AsNoTracking()
                                join membership in db.UserRoles on user.Id equals membership.UserId
                                join role in db.Roles on membership.RoleId equals role.Id
                                where role.Name == "Admin" && user.IsActive && user.IsProtected
                                select user).ToListAsync(timeout.Token);
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApplicationUser>>();
            return admins.Count > 0 && admins.All(user => user.PasswordHash is not null &&
                hasher.VerifyHashedPassword(user, user.PasswordHash, "Admin1!") == PasswordVerificationResult.Failed);
        }
        catch (Exception)
        {
            // Dependency details and credentials must never enter unauthenticated health responses/logs.
            return false;
        }
    }

    /// <summary>Requires the exact compiled EF migration set and the pinned Quartz schema.</summary>
    internal static async Task ValidateSchemaAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        await ValidateExtensionsAsync(db, cancellationToken);
        var expected = db.Database.GetMigrations().ToArray();
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        if (!expected.SequenceEqual(applied)) throw new InvalidOperationException("Database migration required.");
        await QuartzSchemaInstaller.ValidateAsync(db.Database.GetDbConnection(), cancellationToken);
    }

    /// <summary>Requires provisioned extensions; the application role does not acquire superuser rights.</summary>
    internal static async Task ValidateExtensionsAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var count = await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_extension WHERE extname IN ('postgis', 'citext')")
            .SingleAsync(cancellationToken);
        if (count != 2) throw new InvalidOperationException("Database extensions must be provisioned before maintenance.");
    }
}
