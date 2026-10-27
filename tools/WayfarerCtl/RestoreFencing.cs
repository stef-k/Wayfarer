using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Persist actual restart policies before disabling them; never stop an unowned volume consumer.</summary>
public sealed class RestoreFencing(IProcessRunner runner)
{
    /// <summary>Record the complete container set first, then disable every restart policy before stopping writers.</summary>
    public async Task<RestoreReceipt> FenceAsync(string root, RestoreReceipt receipt, CancellationToken token)
    {
        var config = receipt.Plan.Target;
        var owner = new RestoreContainers(runner);
        var ids = (await owner.Required(["ps", "-aq", "--no-trunc", "--filter", "label=com.docker.compose.project=" + config.Project], token))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var policies = new Dictionary<string, string>();
        var database = new HashSet<string>();
        foreach (var id in ids)
        {
            using var document = JsonDocument.Parse(await owner.Required(["inspect", id], token));
            var container = document.RootElement[0];
            VerifyOwned(receipt, container);
            await VerifyProxyImageAsync(receipt, container, token);
            var service = container.GetProperty("Config").GetProperty("Labels").GetProperty("com.docker.compose.service").GetString();
            if (service is "backup-worker" or "backup-reader" or "backup-destination-check" && container.GetProperty("State").GetProperty("Running").GetBoolean())
                throw new IOException("Active backup worker prevents restore.");
            var restart = container.GetProperty("HostConfig").GetProperty("RestartPolicy");
            policies[id] = restart.GetProperty("Name").GetString()!;
            if (policies[id] == "on-failure") policies[id] += ":" + restart.GetProperty("MaximumRetryCount").GetInt32();
            if (service == "db") database.Add(id);
        }
        foreach (var role in new[] { "app-data", "db-data" })
        {
            var consumers = (await owner.Required(["ps", "-aq", "--no-trunc", "--filter", "volume=" + ActiveStorage.Volume(config, role)], token))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var consumer in consumers.Except(ids))
            {
                using var document = JsonDocument.Parse(await owner.Required(["inspect", consumer], token));
                if (!IsRetainedHelper(root, config, document.RootElement[0]))
                    throw new IOException("Unknown durable volume consumer prevents restore.");
                // Already stopped with restart=no: retain this helper under its original receipt owner.
            }
        }
        receipt = receipt with { RestartPolicies = policies, Containers = policies.Keys.ToArray() };
        receipt.Save(root);
        foreach (var id in ids) await owner.Required(["update", "--restart=no", id], token);
        foreach (var id in ids.Except(database)) await owner.Required(["stop", "--time", "70", id], token);
        return receipt;
    }

    /// <summary>Stopped helpers from retained receipt history may share the active generation but never regain restart authority.</summary>
    internal static bool IsRetainedHelper(string root, Deployment config, JsonElement container)
    {
        if (container.GetProperty("State").GetProperty("Running").GetBoolean() ||
            container.GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString() != "no") return false;
        if (IsUpdateHelper(root, config, container)) return true;
        var directory = Path.Combine(root, "recovery-control", "restore-history");
        var paths = new[] { RestoreReceipt.PathFor(root) }.Where(File.Exists).Concat(Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").Take(100) : Array.Empty<string>());
        var name = container.GetProperty("Name").GetString()!.TrimStart('/');
        var id = container.GetProperty("Id").GetString()!;
        foreach (var path in paths)
        {
            ProtectedFiles.SafePath(path);
            ProtectedFiles.Check(path, 0);
            if (new FileInfo(path).Length > 262144) throw new IOException("Retained receipt exceeds bound.");
            var previous = JsonSerializer.Deserialize<RestoreReceipt>(File.ReadAllText(path), WayfarerRecovery.ArchiveContract.Json)
                ?? throw new IOException("Invalid retained receipt.");
            previous.Validate(root);
            if (previous.Plan.Target.Project != config.Project || !previous.Containers.Contains(name) && !previous.Containers.Contains(id)) continue;
            VerifyOwned(previous, container);
            return true;
        }
        return false;
    }

    /// <summary>A stopped retained migration helper remains receipted evidence, not a foreign writable consumer.</summary>
    private static bool IsUpdateHelper(string root, Deployment config, JsonElement container)
    {
        var helperLabels = container.GetProperty("Config").GetProperty("Labels");
        if (helperLabels.ValueKind == JsonValueKind.Object && helperLabels.TryGetProperty("wayfarer.update", out var updateLabel) &&
            Guid.TryParseExact(updateLabel.GetString(), "D", out var updateOperation) &&
            UpdateReceipt.Find(root, updateOperation) is { } update &&
            container.GetProperty("Id").GetString() == update.MigrationContainerId &&
            update.Plan.Current.Project == config.Project &&
            container.GetProperty("Config").GetProperty("Image").GetString() == "ghcr.io/stef-k/wayfarer@" + update.Plan.Target.AppDigest &&
            container.GetProperty("Config").GetProperty("Labels").GetProperty("wayfarer.update").GetString() == update.Plan.Operation.ToString("D") &&
            container.GetProperty("Mounts").EnumerateArray().All(mount => !mount.GetProperty("RW").GetBoolean() || mount.GetProperty("Type").GetString() == "tmpfs"))
            return !container.GetProperty("State").GetProperty("Running").GetBoolean() &&
                container.GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString() == "no";
        return false;
    }

    /// <summary>Resume may restart only the intact recorded old DB for a fresh emergency capture, with restart still disabled.</summary>
    public async Task StartEmergencyDatabaseAsync(string root, RestoreReceipt receipt, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        using var recovery = new WayfarerRecovery.RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        var id = (await owner.Required(receipt.Plan.Target.Compose(root, "ps", "-aq", "db"), token)).Trim();
        if (!receipt.RestartPolicies.ContainsKey(id)) throw new IOException("Recorded emergency database is unavailable.");
        using (var document = JsonDocument.Parse(await owner.Required(["inspect", id], token))) VerifyOwned(receipt, document.RootElement[0]);
        await owner.Required(["update", "--restart=no", id], token);
        await owner.Required(["start", id], token);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var ready = await runner.RunAsync(["exec", id, "pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "wayfarer"], null, token);
            if (ready.Code == 0) return;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        throw new IOException("Emergency database did not become ready.");
    }

    /// <summary>Stop only the exact recorded owned containers; uncertainty keeps durable intent unresolved.</summary>
    public async Task StopOwnedAsync(RestoreReceipt receipt, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        foreach (var id in receipt.Containers)
        {
            var inspection = await runner.RunAsync(["inspect", id], null, token);
            if (inspection.Code != 0)
            {
                var all = await owner.Required(["ps", "-a", "--format", "{{.Names}}"], token);
                if (all.Split('\n').Contains(id)) throw new IOException("Owned container state uncertain.");
                continue;
            }
            using (var document = JsonDocument.Parse(inspection.Output))
            {
                VerifyOwned(receipt, document.RootElement[0]);
                await VerifyProxyImageAsync(receipt, document.RootElement[0], token);
            }
            await owner.Required(["update", "--restart=no", id], token);
            await owner.Required(["stop", "--time", "30", id], token);
            await owner.Required(["wait", id], token);
        }
    }
    /// <summary>Names from durable intent never authorize stopping a replacement foreign container or wrong durable mount.</summary>
    public static void VerifyOwned(RestoreReceipt receipt, JsonElement container)
    {
        var config = receipt.Plan.Target;
        if (IsUpdateHelper(receipt.Plan.Root, config, container)) return;
        var labels = container.GetProperty("Config").GetProperty("Labels");
        string? Label(string key) => labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(key, out var value) ? value.GetString() : null;
        if (Label("com.docker.compose.project") != config.Project && Label("wayfarer.restore-helper") != config.Project)
            throw new IOException("Recorded container was replaced by foreign authority.");
        var image = container.GetProperty("Config").GetProperty("Image").GetString();
        string? updateImage = null;
        Deployment? replacement = null;
        if (receipt.Plan.FromUpdate is not null)
        {
            UpdateRestoreHandoff.Require(receipt.Plan.Root, receipt.Plan);
            var update = UpdateReceipt.Find(receipt.Plan.Root, receipt.Plan.FromUpdate.Value)!;
            replacement = update.Plan.UpdateProtocol == 2 && update.Phase >= UpdatePhase.PostflightConfirmed ? update.Plan.Target : null;
            updateImage = "ghcr.io/stef-k/wayfarer@" + update.Plan.Target.AppDigest;
        }
        if (image != updateImage && image != "ghcr.io/stef-k/wayfarer@" + config.AppDigest && image != "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest &&
            image != "caddy@" + config.CaddyDigest && (replacement is null || image != "caddy@" + replacement.CaddyDigest))
            throw new IOException("Recorded container image identity changed.");
        if (Label("com.docker.compose.service") == "caddy")
        {
            var proxy = image == "caddy@" + config.CaddyDigest ? config : replacement!;
            try { CaddyReplacement.VerifyProxy(receipt.Plan.Root, proxy, container); }
            catch (UsageException) when (receipt.Phase >= RestorePhase.ActivationIntent && proxy == config)
            { CaddyReplacement.VerifyProxy(receipt.Plan.Root, RestoreCandidate.Configuration(receipt.EffectivePlan), container); }
        }
        var allowed = receipt.Volumes.Concat(new[] { "db-data", "app-data", "app-cache", "app-logs", "caddy-data", "caddy-config" }
            .Select(role => ActiveStorage.Volume(config, role))).ToHashSet(StringComparer.Ordinal);
        var service = Label("com.docker.compose.service");
        foreach (var mount in container.GetProperty("Mounts").EnumerateArray())
        {
            var target = mount.GetProperty("Destination").GetString();
            var durable = target is "/var/lib/wayfarer" or "/var/cache/wayfarer" or "/source" or "/candidate" or "/cache" ||
                target == "/var/lib/postgresql" && (service == "db" || container.GetProperty("Name").GetString()!.EndsWith("-db", StringComparison.Ordinal));
            if (durable && (mount.GetProperty("Type").GetString() != "volume" || !allowed.Contains(mount.GetProperty("Name").GetString()!)))
                throw new IOException("Recorded durable mount identity changed.");
        }
    }

    /// <summary>Failure fencing independently checks the native payload for either side of the receipted bridge.</summary>
    private async Task VerifyProxyImageAsync(RestoreReceipt receipt, JsonElement container, CancellationToken token)
    {
        var actual = container.GetProperty("Config");
        if (actual.GetProperty("Labels").GetProperty("com.docker.compose.service").GetString() != "caddy") return;
        var config = receipt.Plan.Target;
        if (actual.GetProperty("Image").GetString() != "caddy@" + config.CaddyDigest && receipt.Plan.FromUpdate is { } operation)
            config = UpdateReceipt.Find(receipt.Plan.Root, operation)!.Plan.Target;
        await CaddyReplacement.VerifyImageAsync(runner, config, container, token);
    }
}
