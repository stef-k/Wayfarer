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
        var ids = (await owner.Required(["ps", "-aq", "--filter", "label=com.docker.compose.project=" + config.Project], token))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var policies = new Dictionary<string, string>();
        var database = new HashSet<string>();
        foreach (var id in ids)
        {
            using var document = JsonDocument.Parse(await owner.Required(["inspect", id], token));
            var container = document.RootElement[0];
            Preflight.VerifyRetainedResource(config, "container", container, root);
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
            var consumers = (await owner.Required(["ps", "-aq", "--filter", "volume=" + ActiveStorage.Volume(config, role)], token))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (consumers.Except(ids).Any()) throw new IOException("Unknown durable volume consumer prevents restore.");
        }
        receipt = receipt with { RestartPolicies = policies, Containers = ids };
        receipt.Save(root);
        foreach (var id in ids) await owner.Required(["update", "--restart=no", id], token);
        foreach (var id in ids.Except(database)) await owner.Required(["stop", "--time", "70", id], token);
        return receipt;
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
            await owner.Required(["update", "--restart=no", id], token);
            await owner.Required(["stop", "--time", "30", id], token);
            await owner.Required(["wait", id], token);
        }
    }
}
