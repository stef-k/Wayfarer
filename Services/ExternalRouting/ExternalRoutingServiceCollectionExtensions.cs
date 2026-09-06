using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Registers the bounded external-routing slice without growing application startup orchestration.</summary>
public static class ExternalRoutingServiceCollectionExtensions
{
    /// <summary>Adds personal-provider routing, proposal, and acceptance responsibilities.</summary>
    public static IServiceCollection AddExternalRouting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IRoutingDnsResolver, RoutingDnsResolver>();
        services.AddSingleton<RoutingEndpointPolicy>();
        services.AddSingleton<RoutingPinnedTransport>();
        services.AddSingleton<IRoutingPinnedTransport>(provider => provider.GetRequiredService<RoutingPinnedTransport>());
        services.AddSingleton<RoutingBoundedExecutor>();
        services.AddSingleton<RoutingRequestBudget>();
        services.AddSingleton<RoutingProviderPacer>();
        services.AddScoped<RoutingAttemptCoordinator>();
        services.AddSingleton<IProviderRouteGeometryValidator, ProviderRouteGeometryValidator>();
        services.AddScoped<AuthoritativeRoutingProviderResolver>();
        services.AddScoped<IProviderRouteClient, ProviderRouteClient>();
        services.AddScoped<ExternalRouteProposalContextService>();
        services.AddScoped<ExternalRouteProposalGenerator>();
        services.AddScoped<ExternalRouteProposalSaveValidator>();
        services.AddScoped<ExternalRoutingCapabilityProjector>();
        services.AddScoped<MobileRoutingService>();
        services.AddScoped<MobileRoutingProfileDiscoveryService>();
        services.AddScoped<PersonalProviderSetupService>();
        return services;
    }
}
