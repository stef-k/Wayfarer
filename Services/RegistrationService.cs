using Wayfarer.Models;

namespace Wayfarer.Parsers;

/// <summary>Authoritative registration policy, checked before any account work.</summary>
public interface IRegistrationService
{
    /// <summary>Only an explicitly open settings row permits registration.</summary>
    bool IsRegistrationOpen();
}

/// <summary>Reads the application registration policy; missing settings fail closed.</summary>
public class RegistrationService(ApplicationDbContext context) : IRegistrationService
{
    /// <inheritdoc />
    public bool IsRegistrationOpen() =>
        context.ApplicationSettings.OrderBy(s => s.Id).FirstOrDefault()?.IsRegistrationOpen == true;
}
