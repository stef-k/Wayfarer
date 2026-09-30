using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.CommandLine;

/// <summary>Read-only recovery authority inspection without web, jobs, migrations or key generation.</summary>
internal static class RecoverySourceCli
{
    /// <summary>Resolve real application authorities and emit bounded structured compatibility facts.</summary>
    internal static async Task<int> RunAsync(TextWriter output, TextWriter error)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            var secretOwner = typeof(ApplicationDbContext).Assembly.GetType("Wayfarer.Services.DatabaseSecret", throwOnError: true)!;
            secretOwner.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [builder.Configuration]);
            var storage = new StoragePaths(Options.Create(builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new()), builder.Environment);
            var ring = DataProtectionAuthority.ResolveKeyRing(builder.Configuration, builder.Environment);
            if (storage.DataRoot != "/var/lib/wayfarer" || !ring.Path.StartsWith(storage.DataRoot + "/", StringComparison.Ordinal) ||
                !Directory.Exists(storage.Uploads) || !Directory.Exists(ring.Path) ||
                storage.TempRoot.StartsWith(storage.DataRoot + "/", StringComparison.Ordinal) ||
                ring.Path == storage.Uploads || ring.Path.StartsWith(storage.Uploads + "/", StringComparison.Ordinal))
                throw new IOException("Unsupported Compose source authority.");
            builder.Services.AddDataProtection().SetApplicationName(DataProtectionAuthority.StableApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(ring.Path)).DisableAutomaticKeyGeneration();
            builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, PasswordHasher<ApplicationUser>>();
            builder.Services.AddScoped<PersonalProviderCredentialService>();
            builder.Services.AddScoped<StableIdentityReadiness>();
            builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(
                builder.Configuration.GetConnectionString("DefaultConnection"), postgres => postgres.UseNetTopologySuite()));
            await using var app = builder.Build();
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // The payload runs against the immutable image's own readiness owner, including older bundles.
            var readiness = typeof(ApplicationDbContext).Assembly.GetType("Wayfarer.Services.ApplicationReadiness", throwOnError: true)!;
            var validate = readiness.GetMethod("ValidateSchemaAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new IOException("Unsupported application inspection capability.");
            await (Task)validate.Invoke(null, [db, deadline.Token])!;
            var ready = readiness.GetMethod("IsReadyAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new IOException("Unsupported secure readiness capability.");
            if (!await (Task<bool>)ready.Invoke(null, [app.Services, deadline.Token])!)
                throw new IOException("Secure administrator/reference state is not ready.");
            var databaseReady = await db.Database.SqlQueryRaw<bool>("""
                SELECT (current_setting('server_version') = '18.6 (Debian 18.6-1.pgdg12+2)'
                    AND current_database()='wayfarer' AND pg_encoding_to_char(encoding)='UTF8'
                    AND datcollate='C.UTF-8' AND datctype='C.UTF-8' AND datlocprovider='c' AND datlocale IS NULL
                    AND (SELECT extversion FROM pg_extension WHERE extname='postgis')='3.6.4'
                    AND postgis_lib_version()='3.6.4'
                    AND (SELECT extversion FROM pg_extension WHERE extname='citext')='1.8'
                    AND current_user='wayfarer'
                    AND NOT (SELECT rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname=current_user)
                    AND NOT EXISTS (SELECT FROM pg_tables WHERE schemaname='public'
                        AND tablename <> 'spatial_ref_sys' AND tableowner <> 'wayfarer')) AS "Value"
                FROM pg_database WHERE datname=current_database()
                """).SingleAsync(deadline.Token);
            if (!databaseReady) throw new IOException("Database identity/ownership differs from restore contract.");
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Wayfarer.Recovery.Inspection.v1");
            if (protector.Unprotect(protector.Protect("ready")) != "ready") throw new IOException("Unusable ring.");
            var credentialStatus = await scope.ServiceProvider.GetRequiredService<StableIdentityReadiness>().StatusAsync(deadline.Token);
            if (!credentialStatus.Ready)
                throw new IOException("Stable authority is not ready.");
            // Read the loaded image's owner, never a contract compiled into the additive inspection payload.
            var quartzOwner = typeof(ApplicationDbContext).Assembly.GetType("QuartzSchemaInstaller", throwOnError: true)!;
            var contract = quartzOwner.GetProperty("RecoveryCompatibilityContract", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as string;
            // Keep legacy inspection available for already-configured targets and historical application images.
            var quartz = await db.Database.SqlQueryRaw<string>(Wayfarer.Util.QuartzSnapshot.LegacySql).SingleAsync(deadline.Token);
            var snapshot = await db.Database.SqlQueryRaw<string>(Wayfarer.Util.QuartzSnapshot.CanonicalSql).SingleAsync(deadline.Token);
            var version = new AppVersionProvider().Version;
            var revision = typeof(ApplicationDbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+').Last() ?? "";
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Schema = contract is null ? 1 : 2, ProtectedCredentials = credentialStatus.Active == 0 ? "none present" : "readable", Uploads = Path.GetRelativePath(storage.DataRoot, storage.Uploads),
                Ring = Path.GetRelativePath(storage.DataRoot, ring.Path), ApplicationVersion = version,
                SourceRevision = revision, ApplicationName = DataProtectionAuthority.StableApplicationName,
                ExpectedMigrations = db.Database.GetMigrations().ToArray(),
                QuartzIdentity = quartz, QuartzCompatibilityContract = contract, QuartzSnapshotFingerprint = snapshot
            }));
            return 0;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Recovery source inspection failed; verify resolved storage, stable credentials and exact schema.");
            return 1;
        }
    }
}
