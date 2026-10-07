using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Util;

/// <summary>Owns the minimal missing Identity/settings prerequisites without changing existing accounts.</summary>
public static class BrowserFixtureEnvironment
{
    /// <summary>Handles only managed preparation, private key selection and exact prerequisite cleanup.</summary>
    public static async Task<bool> TryRunAsync(string command, string path, ApplicationDbContext db)
    {
        if (command == "key-ring")
        {
            var builder = BrowserFixtureGuard.ApplicationBuilder();
            var ring = DataProtectionAuthority.ResolveKeyRing(builder.Configuration, builder.Environment);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { path = ring.Path }));
            return true;
        }
        if (command == "check")
        {
            var keyPath = Environment.GetEnvironmentVariable("DataProtection__KeyRingPath")
                ?? throw new InvalidOperationException("Managed private key authority is required.");
            var provider = DataProtectionProvider.Create(new DirectoryInfo(keyPath),
                options => options.SetApplicationName(DataProtectionAuthority.StableApplicationName));
            var protector = provider.CreateProtector("Wayfarer.BrowserFixture.Probe");
            if (protector.Unprotect(protector.Protect("ready")) != "ready")
                throw new InvalidOperationException("Browser key authority cannot round-trip.");
            var status = await new StableIdentityReadiness(db, new PersonalProviderCredentialService(provider)).StatusAsync();
            if (status.Pending != 0 || status.Blocked != 0)
                throw new InvalidOperationException("Guarded database protected credentials require their existing key authority.");
            Console.WriteLine("Guarded PG18 application connection and private stable key authority verified.");
            return true;
        }
        if (command == "migrate")
        {
            // Only the guarded database can reach here. Quartz preparation is the existing owner.
            await db.Database.MigrateAsync();
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddSingleton(db);
            await using var provider = services.BuildServiceProvider();
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(provider);
            return true;
        }
        if (command == "prepare")
        {
            await PrepareAsync(db, path);
            return true;
        }
        if (command is "cleanup-environment" or "verify-environment")
        {
            var manifest = JsonSerializer.Deserialize<Preparation>(await File.ReadAllTextAsync(path))!;
            if (command == "cleanup-environment") await CleanupAsync(db, manifest);
            await VerifyAsync(db, manifest);
            return true;
        }
        return false;
    }

    /// <summary>Captures exact missing rows before insertion; transaction rollback preserves prior authority.</summary>
    private static async Task PrepareAsync(ApplicationDbContext db, string path)
    {
        var run = Environment.GetEnvironmentVariable("WAYFARER_E2E_RUN_ID")
            ?? throw new InvalidOperationException("Managed run identity required.");
        var password = Environment.GetEnvironmentVariable("WAYFARER_E2E_PASSWORD")
            ?? throw new InvalidOperationException("Managed ephemeral password required.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var roles = new List<IdentityRole>();
        foreach (var name in new[] { "User", "Admin", "Manager" })
            if (!await db.Roles.AnyAsync(role => role.NormalizedName == name.ToUpperInvariant()))
                roles.Add(new IdentityRole(name) { NormalizedName = name.ToUpperInvariant() });
        db.Roles.AddRange(roles);
        var settings = await db.ApplicationSettings.AnyAsync() ? null : new ApplicationSettings
        {
            Id = 1, LocationTimeThresholdMinutes = 5, LocationDistanceThresholdMeters = 15,
            MaxCacheTileSizeInMB = ApplicationSettings.DefaultMaxCacheTileSizeInMB,
            TileMetadataHotCacheSizeMB = ApplicationSettings.DefaultTileMetadataHotCacheSizeMB,
            UploadSizeLimitMB = ApplicationSettings.DefaultUploadSizeLimitMB, IsRegistrationOpen = false
        };
        if (settings != null) db.ApplicationSettings.Add(settings);
        if (!await db.ActivityTypes.AnyAsync())
            throw new InvalidOperationException("Prepare the maintained guarded ActivityTypes prerequisite first.");
        // Development's legacy seeder recognizes this existing name and cannot create Admin1!.
        var admin = await db.Users.AnyAsync(user => user.NormalizedUserName == "ADMIN") ? null : new ApplicationUser
        {
            Id = $"browser-admin-{run}", UserName = "admin", NormalizedUserName = "ADMIN",
            DisplayName = "Browser prerequisite", IsActive = true, IsProtected = true,
            SecurityStamp = Guid.NewGuid().ToString("N"), ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };
        if (admin != null)
        {
            admin.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(admin, password);
            db.Users.Add(admin);
            var role = roles.SingleOrDefault(role => role.Name == "Admin")
                ?? await db.Roles.SingleAsync(role => role.NormalizedName == "ADMIN");
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = admin.Id, RoleId = role.Id });
        }
        // Never reset or rehash an existing administrator to make Production readiness pass.
        var existing = await (from user in db.Users.AsNoTracking()
                              join link in db.UserRoles on user.Id equals link.UserId
                              join role in db.Roles on link.RoleId equals role.Id
                              where role.Name == "Admin" && user.IsActive && user.IsProtected
                              select user).ToListAsync();
        if (existing.Any(user => user.PasswordHash == null || new PasswordHasher<ApplicationUser>()
                .VerifyHashedPassword(user, user.PasswordHash, "Admin1!") != PasswordVerificationResult.Failed))
            throw new InvalidOperationException("Existing guarded administrator has an unsafe bootstrap password.");
        var manifest = new Preparation(run, admin?.Id, roles.Select(role => role.Id).ToArray(), settings?.Id, false);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest));
        await db.SaveChangesAsync();
        if (settings != null)
        {
            // Capture the creation tuple while our transaction still owns its insert lock.
            var version = await db.Database.SqlQueryRaw<long>(
                "SELECT xmin::text::bigint AS \"Value\" FROM \"ApplicationSettings\" WHERE \"Id\" = 1").SingleAsync();
            manifest = manifest with { SettingsVersion = version };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest));
        }
        await transaction.CommitAsync();
        // Unlike GUID identities, settings ID 1 could belong to a competing transaction after rollback.
        // A crash before this acknowledgement preserves that ambiguous row rather than adopting it.
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest with { Committed = true }));
    }

    /// <summary>Deletes captured missing prerequisites only after all browser writers have stopped.</summary>
    private static async Task CleanupAsync(ApplicationDbContext db, Preparation manifest)
    {
        await db.Users.Where(user => user.Id == manifest.AdminId).ExecuteDeleteAsync();
        foreach (var id in manifest.RoleIds)
        {
            if (await db.UserRoles.AnyAsync(link => link.RoleId == id))
                throw new InvalidOperationException("A prerequisite role acquired foreign members; preserving it.");
            await db.Roles.Where(role => role.Id == id).ExecuteDeleteAsync();
        }
        if (manifest.SettingsId != null)
        {
            if ((!manifest.Committed || manifest.SettingsVersion == null)
                && await db.ApplicationSettings.AnyAsync(settings => settings.Id == manifest.SettingsId))
                throw new InvalidOperationException("Settings commit ownership is unproved; preserving the row.");
            if (manifest.Committed && manifest.SettingsVersion != null)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM \"ApplicationSettings\" WHERE \"Id\" = {manifest.SettingsId.Value} AND xmin::text::bigint = {manifest.SettingsVersion.Value}");
        }
    }

    /// <summary>Independently proves each captured prerequisite identity was removed.</summary>
    private static async Task VerifyAsync(ApplicationDbContext db, Preparation manifest)
    {
        if (await db.Users.AnyAsync(user => user.Id == manifest.AdminId)
            || await db.Roles.AnyAsync(role => manifest.RoleIds.Contains(role.Id))
            || (manifest.SettingsId != null && await db.ApplicationSettings.AnyAsync(settings => settings.Id == manifest.SettingsId)))
            throw new InvalidOperationException("Managed environment cleanup left captured identities.");
        Console.WriteLine("Managed environment cleanup independently verified.");
    }

    /// <summary>Exact identifiers are persisted before mutation, never inferred from a prefix.</summary>
    private sealed record Preparation(string Run, string? AdminId, string[] RoleIds, int? SettingsId, bool Committed,
        long? SettingsVersion = null);
}
