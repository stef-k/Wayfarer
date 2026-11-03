using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Util;
using System.Text.Json;

namespace Wayfarer.CommandLine;

/// <summary>Runs explicit offline maintenance without registering web services or Quartz jobs.</summary>
internal static class LifecycleCli
{
    /// <summary>Recognizes the bounded lifecycle command namespaces.</summary>
    internal static bool Handles(string[] args) => args.Length > 0 &&
        args[0] is "database" or "admin" or "user" or "healthcheck";

    /// <summary>Returns conventional success/failure/usage status and sanitizes operational errors.</summary>
    internal static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        if (args is ["healthcheck"]) return await HealthcheckAsync();
        if (!ValidArguments(args))
        {
            await error.WriteLineAsync("Usage: database migrate|prepare-history|history-status|seed; admin bootstrap|reset <username> --stdin; user find <username>; healthcheck");
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, value) => { value.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            // Maintenance emits only bounded command results, never EF parameters or exceptions.
            builder.Logging.ClearProviders();
            DatabaseSecret.Apply(builder.Configuration);
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"), x => x.UseNetTopologySuite());
                options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            });
            builder.Services.AddIdentityCore<ApplicationUser>(IdentityPasswordPolicy.Configure).AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>().AddDefaultTokenProviders();
            builder.AddWayfarerDataProtection();
            await using var app = builder.Build();
            await using var scope = app.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<ApplicationDbContext>();
            if (args[0] == "database") await DatabaseAsync(args[1], db, services, output, cancellation.Token);
            else if (args[0] == "user") return await FindAsync(args[2], services, output);
            else await AdminAsync(args[1], args[2], input, services);
            if (args is not ["database", "history-status"]) await output.WriteLineAsync("Maintenance completed.");
            return 0;
        }
        catch (HistoryQualificationException)
        {
            await error.WriteLineAsync("History preparation failed: use a qualified target build/runtime/globalization profile; rebuilding cannot qualify it. Keep writers stopped and resume maintenance after correction.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("History preparation interrupted: committed batches remain resumable. Keep writers stopped; use managed update resume or native database prepare-history after schema verification.");
            return 130;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Maintenance failed. Verify schema, history indexes/extension privileges, qualified target rules, database capacity and protected input. Keep writers stopped; reconcile the helper before managed resume, or verify schema before native database prepare-history. No web server or jobs were started.");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    /// <summary>Accepts only documented forms; passwords cannot be supplied in command arguments.</summary>
    private static bool ValidArguments(string[] args) => args is ["database", "migrate" or "prepare-history" or "history-status" or "seed"]
        or ["admin", "bootstrap" or "reset", _, "--stdin"] or ["user", "find", _];

    /// <summary>Owns offline schema then derived-data maintenance; preparation-only resume never replays schema mutation.</summary>
    internal static async Task DatabaseAsync(string operation, ApplicationDbContext db, IServiceProvider services,
        TextWriter output, CancellationToken token)
    {
        await ApplicationReadiness.ValidateExtensionsAsync(db, token);
        if (operation == "history-status")
        {
            var statuses = await new LocationHistoryPreparation(db).InspectAsync(token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { Ready = statuses.All(status => status.Ready), Generations = statuses }));
            return;
        }
        if (operation == "migrate")
        {
            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            if ((await db.Database.GetAppliedMigrationsAsync()).Any(migration => !known.Contains(migration)))
                throw new InvalidOperationException("Unknown migration history.");
            await db.Database.MigrateAsync(token);
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(services);
        }
        else await ApplicationReadiness.ValidateSchemaAsync(db, token);
        if (operation is "migrate" or "prepare-history")
        {
            await output.WriteLineAsync("History preparation: verifying current generations; keep writers stopped until maintenance completes.");
            await new LocationHistoryPreparation(db).EnsureAsync(status => output.WriteLineAsync(
                $"History preparation: {status.Kind}; prepared={status.Prepared}; pending={status.Pending}; ready={status.Ready}"), token);
        }
        else
        {
            await ApplicationDbContextSeed.SeedAsync(services.GetRequiredService<UserManager<ApplicationUser>>(),
                services.GetRequiredService<RoleManager<IdentityRole>>(), services, includeDevelopmentAdmin: false);
        }
    }

    /// <summary>Creates a protected admin once or explicitly resets an existing user using Identity.</summary>
    private static async Task AdminAsync(string operation, string username, TextReader input, IServiceProvider services)
    {
        // Console input must be redirected to avoid terminal echo. Tests supply an explicit reader.
        if (ReferenceEquals(input, Console.In) && !Console.IsInputRedirected)
            throw new InvalidOperationException("Protected redirected stdin required.");
        var password = await input.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024 || password == "Admin1!")
            throw new InvalidOperationException("Invalid protected password.");
        var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.FindByNameAsync(username);
        if (operation == "bootstrap")
        {
            if (user is not null) throw new InvalidOperationException("User already exists; use explicit reset.");
            await using var transaction = await services.GetRequiredService<ApplicationDbContext>().Database.BeginTransactionAsync();
            user = new ApplicationUser { UserName = username, DisplayName = "Wayfarer Administrator", IsActive = true, IsProtected = true };
            EnsureSuccess(await manager.CreateAsync(user, password));
            EnsureSuccess(await manager.AddToRoleAsync(user, "Admin"));
            await transaction.CommitAsync();
        }
        else
        {
            if (user is null) throw new InvalidOperationException("User not found.");
            var token = await manager.GeneratePasswordResetTokenAsync(user);
            EnsureSuccess(await manager.ResetPasswordAsync(user, token, password));
        }
    }

    /// <summary>Emits only the exact matching user's ID and name, suitable for a bounded bridge.</summary>
    private static async Task<int> FindAsync(string username, IServiceProvider services, TextWriter output)
    {
        var user = await services.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync(username);
        if (user is null) return 1;
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new { user.Id, user.UserName }));
        return 0;
    }

    /// <summary>Rejects Identity failures without exposing submitted values.</summary>
    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Identity operation failed.");
    }

    /// <summary>Probes the fixed container loopback endpoint without proxying or following redirects.</summary>
    internal static async Task<int> HealthcheckAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/health/ready");
            var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables().Build();
            var host = configuration["AllowedHosts"]?.Split(';', StringSplitOptions.TrimEntries)
                .FirstOrDefault(value => Uri.CheckHostName(value) != UriHostNameType.Unknown);
            if (host is not null) request.Headers.Host = host;
            using var response = await client.SendAsync(request);
            return response.StatusCode == System.Net.HttpStatusCode.OK ? 0 : 1;
        }
        catch (Exception) { return 1; }
    }
}
