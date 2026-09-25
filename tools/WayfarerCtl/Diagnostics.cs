using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WayfarerCtl;

/// <summary>Bounded read-only evidence; application readiness remains the schema/bootstrap authority.</summary>
public sealed class Diagnostics(IProcessRunner runner, ITerminal terminal)
{
    private bool failed;

    public async Task<int> RunAsync(string root, Deployment config, bool doctor, CancellationToken token, bool finishingSetup = false)
    {
        terminal.Write($"Deployment: {root}\nConfig: {root}/installation.json\nMode: {config.Mode}; hostname: {config.Hostname}; project: {config.Project}");
        await Check("Docker/Compose", async () => await new Preflight(runner).DockerAsync(token));
        await Check("Bundle/config and immutable references", () => { config.CheckBundle(); return Task.CompletedTask; });
        await Check("Secret ownership/modes and consumer copies", () => { Deployment.CheckSecrets(root); return Task.CompletedTask; });
        if (!File.Exists(Path.Combine(root, "setup-complete")) && !finishingSetup)
        {
            failed = true; terminal.Write("FAIL Setup incomplete; follow interrupted-setup recovery guide.");
        }
        else terminal.Write(finishingSetup ? "WARN Final setup verification in progress." : "PASS Setup completion recorded (live checks below remain authoritative).");
        foreach (var service in config.Mode == "managed" ? new[] { "db", "wayfarer", "caddy" } : new[] { "db", "wayfarer" })
            await Check(service + " state/image/mounts", () => ServiceAsync(root, config, service, token));
        await Check("Database/PostGIS", async () =>
        {
            var result = await Required(config.Compose(root, "exec", "-T", "db", "psql", "-U", "postgres", "-d", "wayfarer", "-At", "-c",
                "SELECT current_setting('server_version'), postgis_lib_version(), (SELECT extversion FROM pg_extension WHERE extname='citext');"), token);
            if (!Regex.IsMatch(result.Trim(), @"^17\.[0-9]+[^\r\n]*\|3\.[0-9.]+\|[0-9.]+$")) throw new IOException();
            terminal.Write("DB/PostGIS/citext: " + result.Trim());
        });
        await Check("Application readiness (schema/admin/DB)", async () =>
            await Required(config.Compose(root, "exec", "-T", "wayfarer", "dotnet", "Wayfarer.dll", "healthcheck"), token));
        await Check("Proxy endpoint live/ready", () => EndpointAsync(config, token));
        if (doctor)
        {
            await Check("Expected project volumes/networks", () => ResourcesAsync(config, token));
            await Check("Network collisions", () => new Preflight(runner).NetworksAsync(config, token, installed: true));
            await Check("Data Protection and durable uploads authority", async () =>
                await Required(config.Compose(root, "exec", "-T", "wayfarer", "sh", "-ec",
                    "test -d /var/lib/wayfarer/data-protection; test -r /var/lib/wayfarer/data-protection; test -w /var/lib/wayfarer; test ! -L /var/lib/wayfarer/data-protection; test -n \"$(find /var/lib/wayfarer/data-protection -maxdepth 1 -name 'key-*.xml' -print -quit)\""), token));
        }
        if (config.Mode == "external") terminal.Write("WARN External proxy HTTPS/forwarded-header/public-origin qualification remains administrator-owned.");
        return failed ? 1 : 0;
    }

    private async Task Check(string name, Func<Task> action)
    {
        try { await action(); terminal.Write("PASS " + name); }
        catch (OperationCanceledException) { throw; }
        catch { failed = true; terminal.Write("FAIL " + name + "; check operator troubleshooting."); }
    }

    private async Task<string> Required(string[] command, CancellationToken token)
    {
        var result = await runner.RunAsync(command, null, token);
        if (result.Code != 0) throw new IOException();
        return result.Output;
    }

    /// <summary>Inspect only selected fields; never print raw container configuration/environment.</summary>
    private async Task ServiceAsync(string root, Deployment config, string service, CancellationToken token)
    {
        var id = (await Required(config.Compose(root, "ps", "--all", "--quiet", service), token)).Trim();
        if (id.Length == 0 || id.Contains('\n')) throw new IOException();
        using var json = JsonDocument.Parse(await Required(["inspect", id], token));
        var container = json.RootElement[0];
        var state = container.GetProperty("State");
        if (!state.GetProperty("Running").GetBoolean()) throw new IOException();
        if (service != "caddy" && state.GetProperty("Health").GetProperty("Status").GetString() != "healthy") throw new IOException();
        var image = container.GetProperty("Config").GetProperty("Image").GetString();
        var expected = service switch
        {
            "wayfarer" => "ghcr.io/stef-k/wayfarer@" + config.AppDigest,
            "db" => "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest,
            _ => "caddy@sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b"
        };
        if (image != expected) throw new IOException();
        terminal.Write(service + ": running; image " + expected);
        var mounts = container.GetProperty("Mounts").EnumerateArray().ToArray();
        var targets = service switch
        {
            "wayfarer" => new[] { ("app-data", "/var/lib/wayfarer"), ("app-cache", "/var/cache/wayfarer"), ("app-logs", "/var/log/wayfarer") },
            "db" => new[] { ("db-data", "/var/lib/postgresql/data") },
            _ => new[] { ("caddy-data", "/data"), ("caddy-config", "/config") }
        };
        foreach (var (volume, target) in targets)
            if (!mounts.Any(mount => mount.GetProperty("Type").GetString() == "volume" && mount.GetProperty("Destination").GetString() == target &&
                mount.GetProperty("Name").GetString() == config.Project + "_" + volume && mount.GetProperty("RW").GetBoolean())) throw new IOException();
        CheckPorts(container, config, service);
        if (service == "wayfarer")
        {
            var version = (await Required(config.Compose(root, "exec", "-T", "wayfarer", "dotnet", "Wayfarer.dll", "version"), token)).Trim();
            using var imageJson = JsonDocument.Parse(await Required(["image", "inspect", container.GetProperty("Image").GetString()!], token));
            var label = imageJson.RootElement[0].GetProperty("Config").GetProperty("Labels").GetProperty("org.opencontainers.image.version").GetString();
            if (version != "Wayfarer " + label || !Regex.IsMatch(label ?? "", @"^\d+\.\d+\.\d+$")) throw new IOException();
            terminal.Write("Deployed " + version + " (compiled version agrees with image label)");
        }
    }

    /// <summary>Validate actual listener exposure rather than mistaking the stack's own occupied ports for conflicts.</summary>
    private static void CheckPorts(JsonElement container, Deployment config, string service)
    {
        var ports = container.GetProperty("NetworkSettings").GetProperty("Ports");
        var bound = ports.EnumerateObject().Where(port => port.Value.ValueKind == JsonValueKind.Array).ToArray();
        if (service == "db" || service == "wayfarer" && config.Mode == "managed")
        { if (bound.Length != 0) throw new IOException(); }
        if (service == "wayfarer" && config.Mode == "external")
        {
            if (bound.Length != 1 || bound[0].Name != "8080/tcp") throw new IOException();
            foreach (var endpoint in bound[0].Value.EnumerateArray())
                if (endpoint.GetProperty("HostIp").GetString() != "127.0.0.1" || endpoint.GetProperty("HostPort").GetString() != config.LoopbackPort.ToString()) throw new IOException();
        }
        if (service == "caddy" && !new[] { "80/tcp", "443/tcp", "443/udp" }.All(key => bound.Any(port => port.Name == key))) throw new IOException();
    }

    private async Task ResourcesAsync(Deployment config, CancellationToken token)
    {
        foreach (var network in new[] { "backend", "edge" })
            await Required(["network", "inspect", config.Project + "_" + network], token);
        foreach (var volume in config.Mode == "managed" ? new[] { "app-data", "app-cache", "app-logs", "db-data", "caddy-data", "caddy-config" } : new[] { "app-data", "app-cache", "app-logs", "db-data" })
            await Required(["volume", "inspect", config.Project + "_" + volume], token);
    }

    /// <summary>Normal certificate validation, no redirects/proxy environment; bounded public/loopback proof.</summary>
    public static async Task EndpointAsync(Deployment config, CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var origin = config.Mode == "managed" ? "https://" + config.Hostname : "http://127.0.0.1:" + config.LoopbackPort;
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, origin + path);
            request.Headers.Host = config.Hostname;
            using var response = await client.SendAsync(request, token);
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException();
        }
    }
}
