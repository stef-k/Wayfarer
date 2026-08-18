using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Registers the bounded external-routing slice without growing application startup orchestration.</summary>
public static class ExternalRoutingServiceCollectionExtensions
{
    /// <summary>Adds routing endpoint policy, OSRM, verification, proposal, and acceptance responsibilities.</summary>
    public static IServiceCollection AddExternalRouting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RoutingOutboundOptions>(configuration.GetSection("ExternalRouting:Outbound"));
        services.AddSingleton<IRoutingDnsResolver, RoutingDnsResolver>();
        services.AddSingleton<RoutingEndpointPolicy>();
        services.AddSingleton<RoutingPinnedTransport>();
        services.AddSingleton<IRoutingPinnedTransport>(provider => provider.GetRequiredService<RoutingPinnedTransport>());
        services.AddSingleton<RoutingBoundedExecutor>();
        services.AddSingleton<RoutingRequestBudget>();
        services.AddSingleton<IProviderRouteGeometryValidator, ProviderRouteGeometryValidator>();
        services.AddScoped<RoutingProviderCredentialService>();
        services.AddScoped<IOsrmRouteClient, OsrmRouteClient>();
        services.AddScoped<IRoutingProviderVerifier, RoutingProviderVerifier>();
        services.AddScoped<RoutingProviderActivationService>();
        services.AddScoped<ExternalRouteProposalContextService>();
        services.AddScoped<ExternalRouteProposalGenerator>();
        services.AddScoped<ExternalRouteProposalAcceptanceService>();
        return services;
    }
}
