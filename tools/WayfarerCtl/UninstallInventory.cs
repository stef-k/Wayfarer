using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Read-only Docker discovery freezes exact identities after current/terminal protected ownership validation.</summary>
internal sealed class UninstallInventory(IProcessRunner runner)
{
    /// <summary>Active roles follow the existing storage owner, including managed proxy state.</summary>
    internal static string[] Roles(Deployment config) => config.Mode == "managed"
        ? ["db-data", "app-data", "app-cache", "app-logs", "caddy-data", "caddy-config"]
        : ["db-data", "app-data", "app-cache", "app-logs"];

    /// <summary>Scheduler absence is explicit whenever its protected policy exists, including disabled policies.</summary>
    internal static string[] Services(Deployment config) => ["db", "wayfarer", .. config.Mode == "managed" ? new[] { "caddy" } : [],
        .. config.Backup is not null ? new[] { "backup-scheduler" } : []];

    /// <summary>Configured recovery storage and shared images are visible exclusions, without Docker deletion identity.</summary>
    internal static UninstallResource[] Exclusions(Deployment config) =>
    [
        new(UninstallResourceKind.Image, "ghcr.io/stef-k/wayfarer@" + config.AppDigest, null, null, null, UninstallAction.Exclude, null),
        new(UninstallResourceKind.Image, "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, null, null, null, UninstallAction.Exclude, null),
        .. config.Mode == "managed" ? new[] { new UninstallResource(UninstallResourceKind.Image,
            "caddy@" + ReleaseContract.CaddyDigest, null, null, null, UninstallAction.Exclude, null) } : [],
        .. config.Backup is not null ? new[] { new UninstallResource(UninstallResourceKind.BackupDestination,
            config.Backup.Destination, null, null, null, UninstallAction.Exclude, null) } : []
    ];

    /// <summary>Inspect every same-project/near-name resource and every retained-volume/network consumer; never silently omit suspicion.</summary>
    internal async Task<UninstallResource[]> DiscoverAsync(string root, Deployment config, UninstallMode mode,
        UninstallHistory history, CancellationToken token)
    {
        var volumes = await ListAsync("volume", config.Project, token);
        var dockerRoot = (await Required(["info", "--format", "{{.DockerRootDir}}"], token)).Trim();
        BackupPolicy.LiteralPath(dockerRoot);
        volumes = volumes.Select(volume => BindVolumeDirectory(dockerRoot, volume)).ToList();
        var networks = await ListAsync("network", config.Project, token);
        var containers = await ListAsync("container", config.Project, token);
        foreach (var update in history.Updates)
        {
            var labelled = await Required(["ps", "-aq", "--no-trunc", "--filter", "label=wayfarer.update=" + update.Plan.Operation.ToString("D")], token);
            foreach (var id in Split(labelled)) containers.Add(await InspectAsync("container", id, token));
        }
        foreach (var (kind, resources) in new[] { ("volume", volumes), ("network", networks) })
        {
            foreach (var resource in resources)
            {
                var name = resource.GetProperty("Name").GetString()!;
                var consumers = await Required(["ps", "-aq", "--no-trunc", "--filter", kind + "=" + name], token);
                foreach (var id in Split(consumers)) containers.Add(await InspectAsync("container", id, token));
            }
        }
        return Build(root, config, mode, history, Unique(containers, UninstallResourceKind.Container),
            Unique(networks, UninstallResourceKind.Network), Unique(volumes, UninstallResourceKind.Volume));
    }

    /// <summary>Use exact inspection records as a pure ownership seam for deterministic tests and later reconciliation.</summary>
    internal static UninstallResource[] Build(string root, Deployment config, UninstallMode mode, UninstallHistory history,
        JsonElement[] containers, JsonElement[] networks, JsonElement[] volumes)
    {
        history.Validate(root, config);
        var result = new Dictionary<(UninstallResourceKind Kind, string Name), UninstallResource>();
        void Add(UninstallResource resource)
        {
            if (!result.TryAdd((resource.Kind, resource.Name), resource)) throw new UsageException("Ambiguous Docker resource identity.");
        }
        var ownedVolumes = history.Volumes(config);
        foreach (var volume in volumes)
        {
            var name = volume.GetProperty("Name").GetString()!;
            if (!ownedVolumes.TryGetValue(name, out var owner)) throw new UsageException("Unprovable same-project volume; cleanup refused.");
            Preflight.VerifyRetainedResource(owner.Config, "volume", volume, root);
            if (volume.GetProperty("Driver").GetString() != "local" || volume.GetProperty("Scope").GetString() != "local" ||
                volume.GetProperty("Options").ValueKind is not JsonValueKind.Null && volume.GetProperty("Options").EnumerateObject().Any())
                throw new UsageException("Non-local or administrator-mounted volume is not installation deletion authority.");
            Add(new(UninstallResourceKind.Volume, name, name, owner.Role, owner.Operation,
                mode == UninstallMode.Normal ? UninstallAction.Retain : UninstallAction.Remove, Evidence(UninstallResourceKind.Volume, volume)));
        }
        foreach (var (name, owner) in ownedVolumes)
            result.TryAdd((UninstallResourceKind.Volume, name), new(UninstallResourceKind.Volume, name, null, owner.Role, owner.Operation,
                mode == UninstallMode.Normal ? UninstallAction.Retain : UninstallAction.Remove, null));
        var helperNetworks = history.Networks();
        foreach (var network in networks)
        {
            var name = network.GetProperty("Name").GetString()!;
            Guid? operation = null;
            if (name == config.Project + "_backend" || name == config.Project + "_edge")
            {
                Preflight.VerifyRetainedResource(config, "network", network, root);
                if (name.EndsWith("_backend", StringComparison.Ordinal) && !network.GetProperty("Internal").GetBoolean())
                    throw new UsageException("Canonical backend network authority changed.");
            }
            else if (helperNetworks.TryGetValue(name, out var owner) && Label(network, "wayfarer.restore-helper") == config.Project &&
                Label(network, "wayfarer.restore") == owner.ToString("D") && network.GetProperty("Internal").GetBoolean()) operation = owner;
            else throw new UsageException("Foreign or unreceipted network; cleanup refused.");
            if (network.GetProperty("Driver").GetString() != "bridge" || network.GetProperty("Scope").GetString() != "local")
                throw new UsageException("Network is not installation-owned local bridge authority.");
            Add(new(UninstallResourceKind.Network, name, network.GetProperty("Id").GetString(), null, operation,
                UninstallAction.Remove, Evidence(UninstallResourceKind.Network, network)));
        }
        foreach (var name in new[] { config.Project + "_backend", config.Project + "_edge" }.Concat(helperNetworks.Keys))
            result.TryAdd((UninstallResourceKind.Network, name), new(UninstallResourceKind.Network, name, null, null,
                helperNetworks.TryGetValue(name, out var operation) ? operation : null, UninstallAction.Remove, null));
        foreach (var container in containers)
        {
            var name = container.GetProperty("Name").GetString()!.TrimStart('/');
            var service = Label(container.GetProperty("Config"), "com.docker.compose.service");
            Guid? operation = null;
            if (service is not null && Services(config).Contains(service) && name == config.Project + "-" + service + "-1" &&
                Label(container.GetProperty("Config"), "com.docker.compose.oneoff") != "True")
            {
                Preflight.VerifyRetainedResource(config, "container", container, root);
                if (service == "backup-scheduler") VerifyRecoveryService(root, config, container, true);
                else UpdateRuntime.VerifyService(root, config, service, container);
                VerifyAdditionalMounts(root, config, service, container);
            }
            else operation = history.Helper(root, config, container);
            Add(new(UninstallResourceKind.Container, name, container.GetProperty("Id").GetString(), service, operation,
                UninstallAction.Remove, Evidence(UninstallResourceKind.Container, container)));
        }
        foreach (var service in Services(config))
        {
            var name = config.Project + "-" + service + "-1";
            result.TryAdd((UninstallResourceKind.Container, name), new(UninstallResourceKind.Container, name, null, service,
                null, UninstallAction.Remove, null));
        }
        if (mode == UninstallMode.Normal && new[] { "db-data", "app-data" }.Any(role =>
            result[(UninstallResourceKind.Volume, ActiveStorage.Volume(config, role))].DockerId is null))
            throw new UsageException("Missing active DB/application storage prevents normal uninstall.");
        foreach (var excluded in Exclusions(config)) Add(excluded);
        return result.Values.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Stable ownership facts exclude health, restart policy, running state and raw secret-bearing environment.</summary>
    internal static string Evidence(UninstallResourceKind kind, JsonElement resource)
    {
        var fields = kind switch
        {
            UninstallResourceKind.Container => new[] { "Id", "Name", "Created", "Image", "Mounts" },
            UninstallResourceKind.Network => ["Id", "Name", "Created", "Driver", "Scope", "Internal", "IPAM", "Options", "Labels"],
            UninstallResourceKind.Volume => ["Name", "CreatedAt", "Driver", "Scope", "Options", "Labels", "Mountpoint", "DataDirectory"],
            _ => throw new UsageException("Excluded resources have no Docker evidence.")
        };
        var facts = fields.Where(name => name != "Labels").ToDictionary(name => name, name => resource.GetProperty(name), StringComparer.Ordinal);
        var labels = kind == UninstallResourceKind.Container ? resource.GetProperty("Config").GetProperty("Labels") : resource.GetProperty("Labels");
        facts.Add("Labels", JsonSerializer.SerializeToElement(labels.ValueKind == JsonValueKind.Object
            ? labels.EnumerateObject().Where(p => p.Name is "com.docker.compose.project" or "com.docker.compose.service" or
                "com.docker.compose.project.config_files" or "com.docker.compose.project.working_dir" or "com.docker.compose.volume" or
                "com.docker.compose.network" or "com.docker.compose.oneoff" or "wayfarer.restore-helper" or "wayfarer.restore" or "wayfarer.update")
                .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal) : new Dictionary<string, JsonElement>()));
        if (kind == UninstallResourceKind.Container)
        {
            var config = resource.GetProperty("Config");
            facts.Add("ConfiguredImage", config.GetProperty("Image"));
            facts.Add("Networks", JsonSerializer.SerializeToElement(resource.GetProperty("NetworkSettings").GetProperty("Networks")
                .EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal).ToArray()));
        }
        return Canonical(JsonSerializer.SerializeToElement(facts));
    }

    /// <summary>Local volumes lack immutable Docker IDs; bind the exact no-follow data inode and birth time as replacement evidence.</summary>
    private static JsonElement BindVolumeDirectory(string dockerRoot, JsonElement volume)
    {
        var name = volume.GetProperty("Name").GetString()!;
        WayfarerRecovery.SafeDirectory.ValidateName(name);
        if (name.Contains('/')) throw new UsageException("Invalid Docker volume name.");
        var path = volume.GetProperty("Mountpoint").GetString()!;
        if (path != Path.Combine(dockerRoot, "volumes", name, "_data"))
            throw new UsageException("Volume mountpoint differs from local Docker storage authority.");
        using var directory = new WayfarerRecovery.SafeDirectory(path);
        var facts = directory.Identity;
        var created = Directory.GetCreationTimeUtc(path);
        if (!facts.Equals(directory.Identity)) throw new UsageException("Volume directory changed during inspection.");
        var values = volume.Deserialize<Dictionary<string, JsonElement>>()!;
        values["DataDirectory"] = JsonSerializer.SerializeToElement(new { facts.DeviceMajor, facts.DeviceMinor, facts.Inode,
            facts.User, facts.Group, facts.Mode, CreatedUtc = created });
        return JsonSerializer.SerializeToElement(values);
    }

    /// <summary>Sort inspect object fields recursively; mount order is stable Docker identity, never ambient dictionary order.</summary>
    private static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Write only the selected inspect projection, with deterministic object ordering.</summary>
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    /// <summary>Read all labels and names for this project; a label cannot hide a foreign near-name resource.</summary>
    private async Task<List<JsonElement>> ListAsync(string kind, string project, CancellationToken token)
    {
        string[] list = kind == "container" ? ["ps", "-a", "--no-trunc", "--format", "{{.Names}}"] : [kind, "ls", "--format", "{{.Name}}"];
        var named = Split(await Required(list, token)).Where(name => name.StartsWith(project + "_", StringComparison.Ordinal) ||
            name.StartsWith(project + "-", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (var label in new[] { "com.docker.compose.project", "wayfarer.restore-helper" })
            named.UnionWith(Split(await Required([.. list, "--filter", "label=" + label + "=" + project], token)));
        if (named.Count > 4096) throw new UsageException("Docker inventory exceeds bound.");
        var result = new List<JsonElement>();
        foreach (var name in named.Order(StringComparer.Ordinal)) result.Add(await InspectAsync(kind, name, token));
        return result;
    }

    /// <summary>Any inspection failure or multi-result response is uncertainty, never proof of absence.</summary>
    private async Task<JsonElement> InspectAsync(string kind, string identity, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await Required([kind, "inspect", identity], token));
        if (document.RootElement.GetArrayLength() != 1) throw new UsageException("Ambiguous Docker inspection.");
        return document.RootElement[0].Clone();
    }

    /// <summary>Consumers discovered twice must report the same stable ownership identity.</summary>
    private static JsonElement[] Unique(IEnumerable<JsonElement> resources, UninstallResourceKind kind)
    {
        var key = kind == UninstallResourceKind.Volume ? "Name" : "Id";
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var id = resource.GetProperty(key).GetString()!;
            if (result.TryGetValue(id, out var previous) && Evidence(kind, previous) != Evidence(kind, resource))
                throw new UsageException("Docker state changed during inventory; retry planning.");
            result[id] = resource;
        }
        return result.Values.ToArray();
    }

    /// <summary>Later destructive retries may reconcile absence, but never accept a recreated name or changed ownership.</summary>
    internal static void Reconcile(UninstallResource planned, UninstallResource actual, bool allowAbsent)
    {
        if (planned.Kind != actual.Kind || planned.Name != actual.Name ||
            actual.DockerId is null && !allowAbsent && planned.DockerId is not null ||
            actual.DockerId is not null && (planned.DockerId != actual.DockerId || planned.Evidence != actual.Evidence))
            throw new UsageException("Planned Docker resource is missing, replaced or changed.");
    }

    /// <summary>A response reaching the process runner's retention cap may be truncated and cannot prove complete inventory or absence.</summary>
    private async Task<string> Required(string[] arguments, CancellationToken token)
    {
        var result = await runner.RunAsync(arguments, null, token);
        if (result.Code != 0 || result.Output.Length >= 262144) throw new UsageException("Cannot prove Docker uninstall ownership.");
        return result.Output;
    }

    /// <summary>Docker list output consists only of one literal identity per line.</summary>
    private static string[] Split(string output) => output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Ownership label access treats absent/null label objects as unowned.</summary>
    private static string? Label(JsonElement resource, string key) => resource.TryGetProperty("Labels", out var labels) &&
        labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(key, out var value) ? value.GetString() : null;

    /// <summary>Proxy/config/log mounts omitted by the update service validator still need exact uninstall ownership.</summary>
    private static void VerifyAdditionalMounts(string root, Deployment config, string service, JsonElement container)
    {
        var expected = service switch
        {
            "db" => new[] { (Path.Combine(root, "secrets/db-app-password"), "/run/secrets/app-password", "bind", true),
                (Path.Combine(config.Bundle, "db/20-wayfarer.sh"), "/docker-entrypoint-initdb.d/20-wayfarer.sh", "bind", true) },
            "wayfarer" => new[] { (ActiveStorage.Volume(config, "app-logs"), "/var/log/wayfarer", "volume", false) },
            "caddy" => [(ActiveStorage.Volume(config, "caddy-data"), "/data", "volume", false),
                (ActiveStorage.Volume(config, "caddy-config"), "/config", "volume", false),
                (Path.Combine(config.Bundle, "caddy/Caddyfile"), "/etc/caddy/Caddyfile", "bind", true)],
            _ => []
        };
        foreach (var (source, target, type, readOnly) in expected) RequireMount(container, source, target, type, readOnly);
        if (service == "caddy" && !container.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject()
            .Select(value => value.Name).SequenceEqual(new[] { config.Project + "_edge" }))
            throw new UsageException("Proxy network authority changed.");
    }

    /// <summary>Recovery service authority is the exact protected generation, payload, destination, source and fixed hardened command.</summary>
    internal static void VerifyRecoveryService(string root, Deployment config, JsonElement container, bool scheduler)
    {
        var policy = config.Backup ?? throw new UsageException("Unconfigured scheduler.");
        var configuration = container.GetProperty("Config");
        var host = container.GetProperty("HostConfig");
        var command = configuration.GetProperty("Cmd").Deserialize<string[]>()!;
        if (configuration.GetProperty("Image").GetString() != "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest ||
            configuration.GetProperty("User").GetString() != "1654:1654" || !host.GetProperty("ReadonlyRootfs").GetBoolean() ||
            host.GetProperty("Privileged").GetBoolean())
            throw new UsageException("Scheduler image changed.");
        if (scheduler ? !command.SequenceEqual(new[] { "schedule" }) : command.Length != 3 || command[0] != "backup" ||
            command[1] != "--host-operation" || !Guid.TryParseExact(command[2], "N", out var operation) || operation == Guid.Empty)
            throw new UsageException("Recovery service command authority changed.");
        RequireMount(container, policy.Payload, "/worker/wayfarer-recovery", "bind", true);
        RequireMount(container, Path.Combine(BackupCompose.DirectoryPath(root, policy), "worker.json"), "/config/worker.json", "bind", true);
        RequireMount(container, policy.Kind == "local" ? policy.Destination : Path.GetDirectoryName(policy.Destination)!,
            policy.Kind == "local" ? "/destination/slot" : "/destination", "bind", false);
        RequireMount(container, ActiveStorage.Volume(config, "app-data"), "/source", "volume", true);
        RequireMount(container, Path.Combine(root, "secrets/app-password"), "/run/secrets/app-password", "bind", true);
        RequireMount(container, Path.Combine(root, "recovery-control"), "/control", "bind", true);
        RequireMount(container, Path.Combine(root, "recovery-control/recovery.lock"), "/control/recovery.lock", "bind", false);
        if (scheduler) RequireMount(container, Path.Combine(root, "recovery-control/state"), "/control/state", "bind", false);
        if (!container.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject()
            .Select(value => value.Name).SequenceEqual(new[] { config.Project + "_backend" }))
            throw new UsageException("Scheduler network authority changed.");
    }

    /// <summary>Mount identity is exact and includes access mode; only named volumes or fixed protected binds qualify.</summary>
    private static void RequireMount(JsonElement container, string source, string target, string type, bool readOnly)
    {
        if (!container.GetProperty("Mounts").EnumerateArray().Any(m => m.GetProperty("Type").GetString() == type &&
            m.GetProperty(type == "volume" ? "Name" : "Source").GetString() == source && m.GetProperty("Destination").GetString() == target &&
            m.GetProperty("RW").GetBoolean() != readOnly)) throw new UsageException("Container mount authority changed.");
    }
}
