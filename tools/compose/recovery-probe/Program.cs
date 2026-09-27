using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

// Test-only resolver uses the image under qualification, not a copied application binary.
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine("/app", name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
await Probe(args.Single());

/// <summary>Seed one unauthorized synthetic credential and prove restored Identity token/credential continuity.</summary>
[MethodImpl(MethodImplOptions.NoInlining)]
static async Task Probe(string operation)
{
    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    typeof(ApplicationDbContext).Assembly.GetType("Wayfarer.Services.DatabaseSecret")!
        .GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [builder.Configuration]);
    builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"), postgres => postgres.UseNetTopologySuite()));
    builder.Services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>()
        .AddEntityFrameworkStores<ApplicationDbContext>().AddDefaultTokenProviders();
    var ring = DataProtectionAuthority.ResolveKeyRing(builder.Configuration, builder.Environment);
    builder.Services.AddDataProtection().SetApplicationName("Wayfarer").PersistKeysToFileSystem(new DirectoryInfo(ring.Path))
        .DisableAutomaticKeyGeneration();
    builder.Services.AddScoped<PersonalProviderCredentialService>();
    await using var app = builder.Build();
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var user = await users.FindByNameAsync("admin") ?? throw new Exception("Fixture user missing.");
    var credentials = scope.ServiceProvider.GetRequiredService<PersonalProviderCredentialService>();
    if (operation == "seed")
    {
        var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "synthetic-recovery-qualification-never-contact-provider");
        db.PersonalLocationProviderProfiles.Add(profile);
        await db.SaveChangesAsync();
        await File.WriteAllTextAsync("/probe/auth-token", await users.GeneratePasswordResetTokenAsync(user));
    }
    else if (operation == "verify")
    {
        var profile = await db.PersonalLocationProviderProfiles.SingleAsync(value => value.UserId == user.Id);
        if (credentials.Read(profile).Credential != "synthetic-recovery-qualification-never-contact-provider") throw new Exception("Credential continuity failed.");
        if (!await users.VerifyUserTokenAsync(user, users.Options.Tokens.PasswordResetTokenProvider, "ResetPassword",
                await File.ReadAllTextAsync("/probe/auth-token"))) throw new Exception("Identity Data Protection continuity failed.");
        Console.WriteLine("PASS protected credential and production Identity token continuity");
    }
    else throw new Exception("Unsupported fixture operation.");
}
