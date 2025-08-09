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
        var cfg  = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var conn = cfg.GetConnectionString("DefaultConnection")
                   ?? throw new InvalidOperationException("Missing DefaultConnection");

        if (OperatingSystem.IsWindows() && conn.StartsWith("Host=/"))
        {
            conn = "Host=localhost;Port=5432;Database=wayfarer;Username=postgres;Password=mesaboogie";
        }
        
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(conn, o => o.UseNetTopologySuite()) 
            .Options;

        var services = new ServiceCollection().BuildServiceProvider();

        return new ApplicationDbContext(options, services);
    }
}