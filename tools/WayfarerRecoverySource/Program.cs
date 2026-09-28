using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Wayfarer.CommandLine;

// Resolve only from the selected immutable application's assembly directory, never the host or destination.
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    if (name.Name is null || name.Name.IndexOfAny(['/', '\\']) >= 0) return null;
    var path = Path.Combine("/app", name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
return args is ["release-contract"] ? ReleaseContract() : args.Length == 0 ? await Inspect() : 2;

// Delay binding application types until the immutable-image resolver is installed.
[MethodImpl(MethodImplOptions.NoInlining)]
static Task<int> Inspect() => RecoverySourceCli.RunAsync(Console.Out, Console.Error);

// Read release-owned code/resources only. No application host, DB connection, key ring or provider is opened.
[MethodImpl(MethodImplOptions.NoInlining)]
static int ReleaseContract()
{
    try
    {
        var assembly = typeof(Wayfarer.Models.ApplicationDbContext).Assembly;
        var quartz = assembly.GetType("QuartzSchemaInstaller", true)!
            .GetProperty("RecoveryCompatibilityContract")?.GetValue(null) as string;
        var migrations = assembly.GetTypes()
            .Select(type => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute>(type)?.Id)
            .Where(id => id is not null).Order(StringComparer.Ordinal).ToArray();
        using var sql = assembly.GetManifestResourceStream("Wayfarer.Scripts.tables_postgres.sql")
            ?? throw new IOException("Missing Quartz resource.");
        var resourceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sql));
        // #701 accepted this product schema across legacy physical layouts. This fixed resource bridge
        // supplies no snapshot and grants no cross-release compatibility: all archive release facts still match exactly.
        if (quartz is null && resourceHash == "b7ebaf090d1ca1a48bef7c662d5a492dee5e376f373074778a3f6b33ff20617b")
            quartz = "wayfarer-quartz-postgres-v1";
        if (quartz is null) throw new IOException("Unsupported historical Quartz release resource.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            CompiledVersion = new Wayfarer.Services.AppVersionProvider().Version,
            Migrations = migrations, TerminalMigration = migrations.Last(), QuartzCompatibilityContract = quartz,
            SupportedLegacySourceSchemas = new[] { 2 },
            QuartzSha256 = resourceHash,
            DataProtectionName = "Wayfarer", Uploads = "uploads", Ring = "data-protection",
            ProtectedCredentialRequirement = "ready"
        }));
        return 0;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Application release contract unavailable.");
        return 1;
    }
}
