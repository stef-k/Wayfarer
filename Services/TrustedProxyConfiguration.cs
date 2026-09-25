using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Wayfarer.Services;

/// <summary>Configures an explicit single-hop proxy authority; absent configuration disables forwarding.</summary>
internal static class TrustedProxyConfiguration
{
    /// <summary>Reads native/container proxy addresses and CIDRs from the same configuration section.</summary>
    internal static void Configure(WebApplicationBuilder builder)
    {
        var proxies = builder.Configuration.GetSection("TrustedProxy:Addresses").Get<string[]>() ?? [];
        var networks = builder.Configuration.GetSection("TrustedProxy:Networks").Get<string[]>() ?? [];
        builder.Services.Configure<ForwardedHeadersOptions>(options => Apply(options, proxies, networks));
    }

    /// <summary>Uses no trust-all fallback, including when both configured lists are empty.</summary>
    internal static void Apply(ForwardedHeadersOptions options, string[] proxies, string[] networks)
    {
        options.ForwardedHeaders = ForwardedHeaders.None;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in proxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
        foreach (var network in networks)
        {
            var parsed = System.Net.IPNetwork.Parse(network);
            if (parsed.PrefixLength == 0) throw new InvalidOperationException("Trusted proxy networks must be bounded.");
            options.KnownIPNetworks.Add(parsed);
        }
        if (proxies.Length + networks.Length > 0)
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto |
                                       ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
    }
}
