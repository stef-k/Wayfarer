using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Read-only host/deployment checks before the first installation write.</summary>
public sealed class Preflight(IProcessRunner runner)
{
    /// <summary>Check runtime platform before invoking Linux ownership APIs.</summary>
    public static void Platform()
    {
        CheckPlatform(OperatingSystem.IsLinux(), RuntimeInformation.OSArchitecture);
    }

    /// <summary>Pure platform contract for deterministic unsupported-host tests.</summary>
    public static void CheckPlatform(bool linux, Architecture architecture)
    {
        if (!linux || architecture != Architecture.X64)
            throw new UsageException("Supported host: Linux AMD64 Docker Engine (local daemon).");
    }

    /// <summary>Fail closed when Docker cannot be reached or Compose lacks required override semantics.</summary>
    public async Task DockerAsync(CancellationToken token)
    {
        var engine = await runner.RunAsync(["info", "--format", "{{.OSType}}/{{.Architecture}}"], null, token);
        if (engine.Code != 0 || engine.Output.Trim() is not ("linux/x86_64" or "linux/amd64"))
            throw new UsageException("Local Linux AMD64 Docker Engine unavailable; check daemon/socket access.");
        var compose = await runner.RunAsync(["compose", "version", "--short"], null, token);
        var version = compose.Output.Trim().TrimStart('v').Split('+', '-')[0];
        if (compose.Code != 0 || !Version.TryParse(version, out var parsed) || parsed < new Version(2, 24, 4) || parsed.Major != 2)
            throw new UsageException("Docker Compose v2 2.24.4+ is required.");
    }

    /// <summary>Validate the real Compose document using temporary non-secret inputs before installation writes.</summary>
    public async Task BundleAsync(string root, Deployment config, CancellationToken token)
    {
        var temporary = Directory.CreateTempSubdirectory("wayfarerctl-preflight-");
        try
        {
            var path = Path.Combine(temporary.FullName, "deployment.env");
            await File.WriteAllTextAsync(path, config.EnvironmentFile(root), token);
            var result = await runner.RunAsync(config.Compose(temporary.FullName, "config", "--format", "json"), null, token);
            if (result.Code != 0) throw new UsageException("Bundle Compose validation failed; restore the trusted bundle/config.");
            using var document = JsonDocument.Parse(result.Output);
            VerifyImages(config, document.RootElement);
        }
        finally { temporary.Delete(recursive: true); }
    }

    /// <summary>Validate resolved images, not just digest-shaped inputs an altered bundle might ignore.</summary>
    public static void VerifyImages(Deployment config, JsonElement document)
    {
        var services = document.GetProperty("services");
        var expected = new Dictionary<string, string>
        {
            ["wayfarer"] = "ghcr.io/stef-k/wayfarer@" + config.AppDigest,
            ["db"] = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest
        };
        if (config.Mode == "managed")
            expected["caddy"] = "caddy@sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b";
        foreach (var (name, image) in expected)
        {
            if (!services.TryGetProperty(name, out var service) || service.GetProperty("image").GetString() != image ||
                service.GetProperty("platform").GetString() != "linux/amd64")
                throw new UsageException("Bundle resolved image/platform differs from accepted immutable configuration.");
        }
    }

    /// <summary>Any labelled resource is existing/partial state, including stopped containers and volumes.</summary>
    public async Task FreshAsync(Deployment config, CancellationToken token)
    {
        foreach (var command in new[] { new[] { "ps", "-aq" }, new[] { "volume", "ls", "-q" }, new[] { "network", "ls", "-q" } })
        {
            var result = await runner.RunAsync([.. command, "--filter", $"label=com.docker.compose.project={config.Project}"], null, token);
            if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Output))
                throw new UsageException("Existing or unverifiable project state: setup refuses mutation. See interrupted-setup recovery.");
        }
        CheckPorts(config);
        await NetworksAsync(config, token);
    }

    /// <summary>Refuse foreign project resources before resuming the receipt-owned Compose sequence.</summary>
    public async Task ResumeAsync(string root, Deployment config, CancellationToken token, bool requireDatabase = false)
    {
        // Include expected names even when a foreign resource has no Compose project label.
        var volumes = await runner.RunAsync(["volume", "ls", "--format", "{{.Name}}"], null, token);
        if (volumes.Code != 0) throw new UsageException("Cannot verify retained volumes.");
        var names = volumes.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (requireDatabase && !names.Contains(config.Project + "_db-data"))
            throw new UsageException("Previously migrated database volume is missing; refusing to recreate durable state.");
        foreach (var kind in new[] { "container", "volume", "network" })
        {
            string[] list = kind == "container" ? ["ps", "-aq"] : [kind, "ls", "-q"];
            var result = await runner.RunAsync([.. list, "--filter", $"label=com.docker.compose.project={config.Project}"], null, token);
            if (result.Code != 0) throw new UsageException("Cannot verify retained project ownership.");
            var ids = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).AsEnumerable();
            if (kind == "volume") ids = ids.Concat(names.Where(name => name.StartsWith(config.Project + "_", StringComparison.Ordinal))).Distinct();
            foreach (var id in ids)
            {
                var inspection = await runner.RunAsync([kind, "inspect", id], null, token);
                if (inspection.Code != 0) throw new UsageException("Cannot inspect retained project ownership.");
                using var document = JsonDocument.Parse(inspection.Output);
                var resource = document.RootElement[0];
                var labels = kind == "container" ? resource.GetProperty("Config").GetProperty("Labels") : resource.GetProperty("Labels");
                string? Label(string key) => labels.TryGetProperty("com.docker.compose." + key, out var value) ? value.GetString() : null;
                if (Label("project") != config.Project) throw new UsageException("Foreign project resource refused.");
                if (kind == "container")
                {
                    var files = Path.Combine(config.Bundle, "compose.yaml") + (config.Mode == "external" ? "," + Path.Combine(config.Bundle, "external.yaml") : "");
                    if (Label("project.working_dir") != config.Bundle || Label("project.config_files") != files ||
                        Label("service") is not ("db" or "wayfarer" or "caddy"))
                        throw new UsageException("Retained container belongs to different Compose inputs.");
                }
                else
                {
                    var name = Label(kind);
                    var allowed = kind == "network" ? new[] { "backend", "edge" } :
                        new[] { "db-data", "app-data", "app-cache", "app-logs", "caddy-data", "caddy-config" };
                    if (name is null || !allowed.Contains(name) || resource.GetProperty("Name").GetString() != config.Project + "_" + name)
                        throw new UsageException("Foreign retained volume/network refused.");
                }
            }
        }
        await NetworksAsync(config, token, installed: true);
    }

    /// <summary>Bind intended listeners briefly; Docker will arbitrate races at actual startup.</summary>
    public static void CheckPorts(Deployment config)
    {
        try
        {
            var address = config.Mode == "managed" ? IPAddress.Any : IPAddress.Loopback;
            foreach (var port in config.Mode == "managed" ? new[] { 80, 443 } : new[] { config.LoopbackPort })
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(address, port));
            }
            if (config.Mode == "managed")
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Any, 443));
            }
        }
        catch (SocketException) { throw new UsageException("Required listener occupied: free managed 80/443 or choose another external loopback port."); }
    }

    /// <summary>Reject overlapping Docker networks and Linux connected/static IPv4 routes.</summary>
    public async Task NetworksAsync(Deployment config, CancellationToken token, bool installed = false)
    {
        var networks = await runner.RunAsync(["network", "ls", "-q"], null, token);
        if (networks.Code != 0) throw new UsageException("Cannot inspect Docker networks.");
        foreach (var id in networks.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var result = await runner.RunAsync(["network", "inspect", id], null, token);
            if (result.Code != 0) throw new UsageException("Cannot inspect a Docker network.");
            using var json = JsonDocument.Parse(result.Output);
            var network = json.RootElement[0];
            var own = network.GetProperty("Labels").ValueKind == JsonValueKind.Object && network.GetProperty("Labels").TryGetProperty("com.docker.compose.project", out var project) && project.GetString() == config.Project;
            if (installed && own) continue;
            var subnets = network.GetProperty("IPAM").GetProperty("Config");
            if (subnets.ValueKind == JsonValueKind.Null) continue;
            foreach (var subnet in subnets.EnumerateArray())
                if (subnet.TryGetProperty("Subnet", out var value) && Overlaps(config.EdgePrefix + ".0/24", value.GetString()!))
                    throw new UsageException("Edge subnet overlaps a Docker network; choose --edge-prefix before setup.");
        }
        // During operation the own bridge route is expected. Fresh setup checks all host routes.
        if (!installed)
            foreach (var line in File.ReadLines("/proc/net/route").Skip(1))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var mask = Convert.ToUInt32(fields[7], 16);
                if (mask == 0) continue;
                var destination = Convert.ToUInt32(fields[1], 16);
                var route = new IPAddress(BitConverter.GetBytes(destination)) + "/" + System.Numerics.BitOperations.PopCount(mask);
                if (Overlaps(config.EdgePrefix + ".0/24", route))
                    throw new UsageException("Edge subnet overlaps a host route; choose --edge-prefix.");
            }
    }

    /// <summary>IPv4 interval comparison includes a more-specific subnet contained in the proposed /24.</summary>
    public static bool Overlaps(string first, string second)
    {
        static (uint Start, uint End)? Range(string text)
        {
            var parts = text.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                return null;
            var bits = int.Parse(parts[1]);
            if (bits is < 0 or > 32) throw new UsageException("Invalid network prefix.");
            var bytes = address.GetAddressBytes();
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
            return (value & mask, value | ~mask);
        }
        var a = Range(first); var b = Range(second);
        return a.HasValue && b.HasValue && a.Value.Start <= b.Value.End && b.Value.Start <= a.Value.End;
    }
}
