using Microsoft.AspNetCore.Identity;

namespace Wayfarer.Services;

/// <summary>Owns the shared password requirements for Production web Identity and maintenance mutations.</summary>
internal static class IdentityPasswordPolicy
{
    /// <summary>Configures Identity validators; UserManager retains password acceptance and mutation authority.</summary>
    internal static void Configure(IdentityOptions options)
    {
        options.Password.RequiredLength = 15;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireDigit = true;
        options.Password.RequireNonAlphanumeric = true;
    }
}
