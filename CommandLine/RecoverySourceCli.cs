using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
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
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Wayfarer.Recovery.Inspection.v1");
            if (protector.Unprotect(protector.Protect("ready")) != "ready") throw new IOException("Unusable ring.");
            if (!(await scope.ServiceProvider.GetRequiredService<StableIdentityReadiness>().StatusAsync(deadline.Token)).Ready)
                throw new IOException("Stable authority is not ready.");
            // Fingerprint the validated schema, allowing the DB-image worker to detect later drift in its dump snapshot.
            var quartz = await db.Database.SqlQueryRaw<string>("""
                SELECT md5(string_agg(table_name || ':' || column_name || ':' || data_type || ':' || is_nullable,
                    '|' ORDER BY table_name, ordinal_position)) AS "Value"
                FROM information_schema.columns WHERE table_schema=current_schema() AND left(table_name,5)='qrtz_'
                """).SingleAsync(deadline.Token);
            var version = new AppVersionProvider().Version;
            var revision = typeof(ApplicationDbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+').Last() ?? "";
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Schema = 1, Uploads = Path.GetRelativePath(storage.DataRoot, storage.Uploads),
                Ring = Path.GetRelativePath(storage.DataRoot, ring.Path), ApplicationVersion = version,
                SourceRevision = revision, ApplicationName = DataProtectionAuthority.StableApplicationName,
                ExpectedMigrations = db.Database.GetMigrations().ToArray(),
                QuartzIdentity = quartz
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
