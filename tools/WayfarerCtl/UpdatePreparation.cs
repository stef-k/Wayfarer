using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Read-only current/target corroboration and protected canonical plan staging.</summary>
public sealed class UpdatePreparation(IProcessRunner runner)
{
    public static string DirectoryFor(string root, Guid operation) => Path.Combine(root, "update-plans", operation.ToString("N"));

    /// <summary>No service is stopped and no application state is written while preparing authorization.</summary>
    public async Task<UpdatePlan> PrepareAsync(string root, string path, CancellationToken token)
    {
        UpdateReceipt.RequireResolved(root);
        RestoreReceipt.RequireResolved(root);
        if (!InstallationCompletion.IsComplete(root) || File.Exists(Path.Combine(root, "backup-transition.json")) ||
            File.Exists(Path.Combine(root, "recovery-control/host-operation.json"))) throw new IOException("Unresolved lifecycle state.");
        var current = Deployment.Load(root);
        if (current.Schema != 4 || current.Release is null || current.Installation == Guid.Empty || current.Backup is not { Enabled: true })
            throw new UsageException("Update requires adopted release authority and an enabled recovery destination.");
        Deployment.CheckSecrets(root);
        var source = ReleaseStore.Select(root, current.Release);
        var target = ReleaseStore.Import(root, path);
        var boundary = UpdateOptions.Boundary(source, target, UpdateOptions.QualificationCandidates(root, current.Project));
        var owner = ReleaseDispatch.CurrentOwner(root, current)!;
        await VerifyAsync(root, current, source, token);
        if (!await new ReleaseImagesVerifier(runner).VerifyAsync(target, token)) throw new IOException("Target images unavailable locally.");
        var next = current with { Bundle = target.Directory, AppDigest = target.Manifest.Images.ApplicationDigest, Release = ReleaseAuthority.From(target) };
        await TopologyAsync(root, current, next, source, target, token);
        var operation = Guid.NewGuid();
        var facts = await InspectAsync(root, current, source, operation, token);
        var capacity = await CapacityAsync(root, current, token);
        var plan = new UpdatePlan(operation, root, current, next, owner, boundary,
            target.Manifest.Application.Migrations.Skip(source.Manifest.Application.Migrations.Length).ToArray(),
            File.ReadAllText(Path.Combine(root, "installation.json")), File.ReadAllText(current.EnvironmentPath(root)),
            ProtectedFiles.SecretsFingerprint(root), capacity) { TargetMigrations = target.Manifest.Application.Migrations };
        var directory = DirectoryFor(root, operation);
        Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Create(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(plan));
        ProtectedFiles.Create(Path.Combine(directory, "source.json"), facts);
        using var parent = new SafeDirectory(directory);
        parent.Flush();
        using var plans = new SafeDirectory(Path.GetDirectoryName(directory)!);
        plans.Flush();
        return plan;
    }

    /// <summary>Only release paths and application image identity may change authority; physical topology and proxy/bootstrap configuration stay fixed.</summary>
    private async Task TopologyAsync(string root, Deployment current, Deployment target, ReleaseBundle source, ReleaseBundle next, CancellationToken token)
    {
        foreach (var path in new[] { "caddy/Caddyfile", "db/20-wayfarer.sh" })
            if (source.Manifest.Files.Single(file => file.Path == path).Sha256 != next.Manifest.Files.Single(file => file.Path == path).Sha256)
                throw new IOException("Update changes DB bootstrap or proxy authority.");
        var preflight = new Preflight(runner);
        var before = await preflight.ResolveAsync(root, current, token);
        var after = await preflight.ResolveAsync(root, target, token);
        foreach (var key in new[] { "networks", "volumes", "secrets" })
            Compare(before, after, key, current.Bundle, target.Bundle);
        foreach (var service in current.Mode == "managed" ? new[] { "db", "wayfarer", "caddy" } : new[] { "db", "wayfarer" })
            foreach (var key in new[] { "networks", "network_mode", "ports", "volumes", "secrets", "user", "privileged", "read_only", "cap_add", "cap_drop" })
                Compare(before.GetProperty("services").GetProperty(service), after.GetProperty("services").GetProperty(service),
                    key, current.Bundle, target.Bundle);
    }

    /// <summary>Normalize only the exact retained bundle directory; installation-owned paths remain literal comparison inputs.</summary>
    private static void Compare(JsonElement before, JsonElement after, string key, string oldBundle, string newBundle)
    {
        var oldExists = before.TryGetProperty(key, out var oldValue);
        var newExists = after.TryGetProperty(key, out var newValue);
        if (oldExists != newExists) throw new IOException("Target deployment topology changed.");
        if (!oldExists) return;
        var oldPrefix = JsonSerializer.Serialize(oldBundle)[1..^1] + "/";
        var newPrefix = JsonSerializer.Serialize(newBundle)[1..^1] + "/";
        using var left = JsonDocument.Parse(oldValue.GetRawText().Replace(oldPrefix, "@bundle/", StringComparison.Ordinal));
        using var right = JsonDocument.Parse(newValue.GetRawText().Replace(newPrefix, "@bundle/", StringComparison.Ordinal));
        if (!JsonElement.DeepEquals(left.RootElement, right.RootElement)) throw new IOException("Target deployment topology changed.");
    }

    /// <summary>Current runtime resource identities and payloads must agree independently with retained release metadata.</summary>
    public async Task VerifyAsync(string root, Deployment config, ReleaseBundle bundle, CancellationToken token)
    {
        bundle.Corroborate(config);
        config.Backup!.CheckPayload();
        bundle.Corroborate(config.Backup.Source);
        BackupCompose.Check(root, config);
        if (!await new ReleaseImagesVerifier(runner).VerifyAsync(bundle, token)) throw new IOException("Retained source images unavailable.");
        await VerifyRuntimeAsync(root, config, token);
        await new Preflight(runner).BundleAsync(root, config, token);
    }

    /// <summary>Inspect active physical resources without rejecting deliberately retained inactive generations.</summary>
    private async Task VerifyRuntimeAsync(string root, Deployment config, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        foreach (var role in new[] { "db-data", "app-data", "app-cache", "app-logs" })
        {
            using var volume = JsonDocument.Parse(await owner.Required(["volume", "inspect", ActiveStorage.Volume(config, role)], token));
            Preflight.VerifyRetainedResource(config, "volume", volume.RootElement[0], root);
        }
        await new Preflight(runner).NetworksAsync(config, token, installed: true);
        foreach (var service in config.Mode == "managed" ? new[] { "db", "wayfarer", "caddy" } : new[] { "db", "wayfarer" })
        {
            var id = (await owner.Required(config.Compose(root, "ps", "-aq", service), token)).Trim();
            if (id.Length == 0 || id.Any(char.IsWhiteSpace)) throw new IOException("Ambiguous current service identity.");
            using var document = JsonDocument.Parse(await owner.Required(["inspect", id], token));
            var container = document.RootElement[0];
            Preflight.VerifyRetainedResource(config, "container", container, root);
            UpdateRuntime.VerifyService(root, config, service, container);
        }
    }

    /// <summary>Application-owned exact EF/Quartz, DB-role, Identity, DP and Uploads validation has only internal DB access.</summary>
    public async Task<string> InspectAsync(string root, Deployment config, ReleaseBundle bundle, Guid operation, CancellationToken token)
    {
        var name = config.Project + "-restore-update-inspect-" + operation.ToString("N") + "-" + Guid.NewGuid().ToString("N");
        var owner = new RestoreContainers(runner);
        var output = await owner.RunAsync(name, [.. Maintenance(root, config), "--volume",
            Path.Combine(bundle.Directory, "WayfarerRecoverySource.dll") + ":/inspection.dll:ro", "--entrypoint=dotnet",
            "ghcr.io/stef-k/wayfarer@" + config.AppDigest, "exec", "--runtimeconfig", "/app/Wayfarer.runtimeconfig.json",
            "--depsfile", "/app/Wayfarer.deps.json", "/inspection.dll"], token);
        await owner.Required(["rm", name], token);
        using var document = JsonDocument.Parse(output);
        var facts = document.RootElement;
        var app = bundle.Manifest.Application;
        if (facts.GetProperty("Schema").GetInt32() != 2 || facts.GetProperty("ApplicationVersion").GetString() != app.CompiledVersion ||
            !facts.GetProperty("ExpectedMigrations").Deserialize<string[]>()!.SequenceEqual(app.Migrations) ||
            facts.GetProperty("QuartzCompatibilityContract").GetString() != app.QuartzCompatibilityContract ||
            facts.GetProperty("ApplicationName").GetString() != app.DataProtectionName ||
            facts.GetProperty("Uploads").GetString() != app.Uploads || facts.GetProperty("Ring").GetString() != app.Ring ||
            facts.GetProperty("ProtectedCredentials").GetString() is not ("none present" or "readable"))
            throw new IOException("Exact update source/target inspection failed.");
        return output;
    }

    /// <summary>Fixed maintenance authority excludes provider/edge networks, admin secrets, writable data and archive storage.</summary>
    public static string[] Maintenance(string root, Deployment config) => [.. RestoreContainers.Unprivileged(),
        "--network", config.Project + "_backend", "--volume", ActiveStorage.Volume(config, "app-data") + ":/var/lib/wayfarer:ro",
        "--volume", Path.Combine(root, "secrets/app-password") + ":/run/secrets/app-password:ro",
        "--env=ConnectionStrings__DefaultConnection=Host=db;Database=wayfarer;Username=wayfarer;Timeout=5",
        "--env=Database__PasswordFile=/run/secrets/app-password"];

    /// <summary>Bound conservative recovery and migration headroom by observed DB size; retain a filesystem operating reserve.</summary>
    public async Task<UpdateCapacity> CapacityAsync(string root, Deployment config, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        var value = await owner.Required(config.Compose(root, "exec", "-T", "db", "psql", "-U", "postgres", "-d", "wayfarer", "-At", "-c",
            "SELECT pg_database_size(current_database());"), token);
        if (!long.TryParse(value.Trim(), out var size) || size <= 0) throw new IOException("Unknown database capacity.");
        var dockerRoot = (await owner.Required(["info", "--format", "{{.DockerRootDir}}"], token)).Trim();
        BackupPolicy.LiteralPath(dockerRoot);
        if (dockerRoot.IndexOfAny([',', ':']) >= 0) throw new IOException("Unsupported Docker storage path.");
        var name = config.Project + "-restore-update-capacity-" + Guid.NewGuid().ToString("N");
        var output = await owner.RunAsync(name, ["--network=none", "--read-only", "--user=0", "--cap-drop=ALL",
            "--cap-add=DAC_READ_SEARCH", "--security-opt=no-new-privileges:true", "--tmpfs=/var/lib/postgresql/data:ro,mode=000,size=65536",
            "--mount", "type=bind,source=" + dockerRoot + ",target=/storage,readonly",
            "--mount", "type=volume,source=" + ActiveStorage.Volume(config, "app-data") + ",target=/files,readonly",
            "--entrypoint=sh", "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, "-ec",
            "df -Pk /storage | tail -1 | awk '{printf \"%s \", $4}'; du -sk /files | awk '{print $1}'"], token);
        await owner.Required(["rm", name], token);
        var observed = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => checked(long.Parse(item, System.Globalization.CultureInfo.InvariantCulture) * 1024)).ToArray();
        if (observed.Length != 2 || observed.Any(item => item < 0)) throw new IOException("Unknown storage capacity.");
        var required = checked(size * 4 + observed[1] * 3 + RestoreCapacity.Reserve * 2);
        RestoreCapacity.Require(root, required);
        RestoreCapacity.Require(config.Backup!.Destination, required);
        RestoreCapacity.Check(observed[0], required);
        using var local = new SafeDirectory(root);
        using var destination = new SafeDirectory(config.Backup.Destination);
        return new UpdateCapacity(required, local.AvailableBytes, destination.AvailableBytes, observed[0], size, observed[1]);
    }

    /// <summary>Only a locally generated protected plan can authorize non-interactive execution.</summary>
    public static UpdatePlan Load(string root, string hash)
    {
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "update-plans")).Take(101))
        {
            var path = Path.Combine(directory, "plan.json");
            ProtectedFiles.SafePath(path);
            ProtectedFiles.Check(path, 0);
            if (new FileInfo(path).Length > 1048576) throw new IOException("Update plan exceeds bound.");
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path), ArchiveContract.Json);
            if (plan is not null && plan.Root == root && plan.Hash() == hash) return plan;
        }
        throw new UsageException("No matching protected update plan.");
    }
}
