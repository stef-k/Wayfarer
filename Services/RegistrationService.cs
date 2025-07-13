using Wayfarer.Models;

namespace Wayfarer.Parsers;

public interface IRegistrationService
{
    void CheckRegistration(HttpContext context);
}

public class RegistrationService : IRegistrationService
{
    private readonly ApplicationDbContext _context; // Assuming you're using EF Core for db access
    private readonly IConfiguration _configuration;

    public RegistrationService(ApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public void CheckRegistration(HttpContext context)
    {
        // Fetch the settings from the database (or cache, depending on your design)
        var settings = _context.ApplicationSettings.FirstOrDefault();
        
        if (settings != null && !settings.IsRegistrationOpen)
        {
            // If registration is closed, redirect to a 'Registration Closed' page
            context.Response.Redirect("/Home/RegistrationClosed");
        }
        // No need to do anything if registration is open, execution will continue
    }
}
