// Design/ApplicationDbContextFactory.cs

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Wayfarer.Models;

namespace Wayfarer.Design;

public class ApplicationDbContextFactory
    : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
                              Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                              "Production";

        var cfg = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var conn = cfg.GetConnectionString("DefaultConnection")
                   ?? throw new InvalidOperationException("Missing DefaultConnection");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(conn, o => o.UseNetTopologySuite()) 
            .Options;

        var services = new ServiceCollection().BuildServiceProvider();

        return new ApplicationDbContext(options, services);
    }
}
