using System.Net;
using Microsoft.Extensions.Options;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Validates routing endpoints against deployment-owned SSRF policy.</summary>
public sealed class RoutingEndpointPolicy
{
    private readonly RoutingOutboundOptions _options;

    /// <summary>Initializes policy from deployment configuration, never database state.</summary>
    public RoutingEndpointPolicy(IOptions<RoutingOutboundOptions> options) => _options = options.Value;

    /// <summary>Validates URI syntax and every resolved address, returning one address to pin.</summary>
    public RoutingEndpointDecision Validate(Uri endpoint, IReadOnlyList<IPAddress> addresses)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.Query.Length != 0 || endpoint.HostNameType == UriHostNameType.Unknown
            || endpoint.Host.Contains('*', StringComparison.Ordinal)
            || endpoint.Scheme is not ("https" or "http") || !PortAllowed(endpoint))
            return RoutingEndpointDecision.Rejected;
        if (addresses.Count == 0 || addresses.Any(address => !AddressAllowed(endpoint, address)))
            return RoutingEndpointDecision.Rejected;
        return new RoutingEndpointDecision(true, addresses[0]);
    }

    /// <summary>Rejects malformed endpoint text before any DNS resolution or request.</summary>
    public RoutingEndpointDecision Validate(string endpoint, IReadOnlyList<IPAddress> addresses) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ? Validate(parsed, addresses) : RoutingEndpointDecision.Rejected;

    private bool PortAllowed(Uri endpoint) => endpoint.IsDefaultPort || _options.AllowedPorts.Contains(endpoint.Port);

    private bool AddressAllowed(Uri endpoint, IPAddress address)
    {
        var restricted = !RoutingIpClassifier.IsPublic(address);
        if (!restricted && endpoint.Scheme == Uri.UriSchemeHttps) return true;
        return _options.SelfHostedAllowlist.Any(entry => entry.Matches(endpoint.Host, address, endpoint.Scheme));
    }
}

/// <summary>Contains deployment-owned intentional self-hosting exceptions.</summary>
public sealed class RoutingOutboundOptions
{
    /// <summary>Gets the exact-host/CIDR entries permitted for intentional self-hosting.</summary>
    public List<RoutingSelfHostedAllowlistEntry> SelfHostedAllowlist { get; init; } = [];

    /// <summary>Gets additional explicitly approved destination ports.</summary>
    public HashSet<int> AllowedPorts { get; init; } = [];
}

/// <summary>Defines one exact host, CIDR, and scheme exception owned by deployment configuration.</summary>
public sealed record RoutingSelfHostedAllowlistEntry(string Host, string Cidr, bool AllowHttp)
{
    /// <summary>Matches all three required exception dimensions.</summary>
    public bool Matches(string host, IPAddress address, string scheme) =>
        host.Equals(Host, StringComparison.OrdinalIgnoreCase) && (scheme == Uri.UriSchemeHttps || AllowHttp)
        && RoutingCidr.Contains(Cidr, address);
}

/// <summary>Represents a safe endpoint decision and the address selected for connection pinning.</summary>
public sealed record RoutingEndpointDecision(bool Allowed, IPAddress? SelectedAddress)
{
    /// <summary>Gets the shared rejection decision.</summary>
    public static RoutingEndpointDecision Rejected { get; } = new(false, null);
}

/// <summary>Classifies public and restricted addresses for routing SSRF enforcement.</summary>
public static class RoutingIpClassifier
{
    /// <summary>Returns true only for globally routable addresses accepted by public policy.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return false;
        var blocked = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? Ipv4Blocked : Ipv6Blocked;
        return blocked.All(cidr => !RoutingCidr.Contains(cidr, address));
    }

    private static readonly string[] Ipv4Blocked = [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15",
        "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4"];
    private static readonly string[] Ipv6Blocked = [
        "::/128", "::1/128", "::ffff:0:0/96", "64:ff9b:1::/48", "100::/64", "2001:db8::/32",
        "fc00::/7", "fe80::/10", "ff00::/8"];
}

/// <summary>Performs exact CIDR membership checks without wildcard host behavior.</summary>
public static class RoutingCidr
{
    /// <summary>Returns whether an address belongs to the supplied CIDR.</summary>
    public static bool Contains(string cidr, IPAddress address)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network)
            || !int.TryParse(parts[1], out var prefix)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (network.AddressFamily != address.AddressFamily) return false;
        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > networkBytes.Length * 8) return false;
        for (var bit = 0; bit < prefix; bit++)
            if ((networkBytes[bit / 8] & (1 << (7 - bit % 8))) != (addressBytes[bit / 8] & (1 << (7 - bit % 8)))) return false;
        return true;
    }
}
