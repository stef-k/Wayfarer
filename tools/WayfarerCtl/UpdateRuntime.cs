using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Update-owned runtime fencing and single-launch migration reconciliation.</summary>
public sealed class UpdateRuntime(IProcessRunner runner)
{
    /// <summary>Persist all observed restart policies before changing any; interrupted fencing never overwrites original evidence.</summary>
    public async Task<UpdateReceipt> FenceAsync(string root, UpdateReceipt receipt, CancellationToken token)
    {
        var config = receipt.Plan.Current;
        var owner = new RestoreContainers(runner);
        if (receipt.Containers.Length == 0)
        {
            var ids = (await owner.Required(["ps", "-aq", "--no-trunc", "--filter", "label=com.docker.compose.project=" + config.Project], token))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var policies = new Dictionary<string, string>();
            foreach (var id in ids)
            {
                using var document = JsonDocument.Parse(await owner.Required(["inspect", id], token));
                var container = document.RootElement[0];
                Preflight.VerifyRetainedResource(config, "container", container, root);
                var restart = container.GetProperty("HostConfig").GetProperty("RestartPolicy");
                policies[id] = restart.GetProperty("Name").GetString()!;
                if (policies[id] == "on-failure") policies[id] += ":" + restart.GetProperty("MaximumRetryCount").GetInt32();
            }
            receipt = receipt with { Containers = ids, RestartPolicies = policies };
            receipt.Save(root);
        }
        await StopWritersAsync(root, receipt, token);
        await RequireExclusiveAsync(receipt, token);
        return receipt;
    }

    /// <summary>Stop only receipted application/proxy/scheduler identities; DB remains available without restart authority.</summary>
    public async Task StopWritersAsync(string root, UpdateReceipt receipt, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        foreach (var id in receipt.Containers)
        {
            var inspection = await runner.RunAsync(["inspect", id], null, token);
            if (inspection.Code != 0) throw new IOException("Receipted update resource unavailable; reconcile before proceeding.");
            using var document = JsonDocument.Parse(inspection.Output);
            var container = document.RootElement[0];
            var labels = container.GetProperty("Config").GetProperty("Labels");
            if (labels.GetProperty("com.docker.compose.project").GetString() != receipt.Plan.Current.Project)
                throw new IOException("Update resource owner changed.");
            var image = container.GetProperty("Config").GetProperty("Image").GetString();
            if (image != "ghcr.io/stef-k/wayfarer@" + receipt.Plan.Current.AppDigest &&
                image != "ghcr.io/stef-k/wayfarer@" + receipt.Plan.Target.AppDigest &&
                image != "ghcr.io/stef-k/wayfarer-db@" + receipt.Plan.Current.DbDigest && image != "caddy@" + ReleaseContract.CaddyDigest)
                throw new IOException("Update resource image changed.");
            await owner.Required(["update", "--restart=no", id], token);
            if (labels.GetProperty("com.docker.compose.service").GetString() != "db")
                await owner.Required(["stop", "--time", "70", id], token);
        }
    }

    /// <summary>Both durable volumes and the DB network must have no foreign or remaining application writers.</summary>
    public async Task RequireExclusiveAsync(UpdateReceipt receipt, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        var config = receipt.Plan.Current;
        foreach (var role in new[] { "app-data", "db-data" })
        {
            var consumers = (await owner.Required(["ps", "-q", "--no-trunc", "--filter", "volume=" + ActiveStorage.Volume(config, role)], token))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var id in consumers)
            {
                using var document = JsonDocument.Parse(await owner.Required(["inspect", id], token));
                var container = document.RootElement[0];
                if (role == "app-data" || !receipt.Containers.Contains(id) ||
                    container.GetProperty("Config").GetProperty("Labels").GetProperty("com.docker.compose.service").GetString() != "db")
                    throw new IOException("Unfenced durable-state consumer.");
            }
        }
        using var network = JsonDocument.Parse(await owner.Required(["network", "inspect", config.Project + "_backend"], token));
        var facts = network.RootElement[0];
        if (!facts.GetProperty("Internal").GetBoolean()) throw new IOException("Database network is not internal.");
        foreach (var peer in facts.GetProperty("Containers").EnumerateObject())
        {
            using var document = JsonDocument.Parse(await owner.Required(["inspect", peer.Name], token));
            var container = document.RootElement[0];
            if (container.GetProperty("State").GetProperty("Running").GetBoolean() &&
                (!receipt.Containers.Contains(peer.Name) || container.GetProperty("Config").GetProperty("Labels")
                    .GetProperty("com.docker.compose.service").GetString() != "db"))
                throw new IOException("Unfenced database network consumer.");
        }
    }

    /// <summary>Persist MigrationStarted before launch, and never launch again from that phase.</summary>
    public async Task<UpdateReceipt> MigrateAsync(string root, UpdateReceipt receipt, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        var preparation = new UpdatePreparation(runner);
        var target = ReleaseStore.Select(root, receipt.Plan.Target.Release!);
        if (receipt.Phase == UpdatePhase.RecoveryVerified)
        {
            await RequireExclusiveAsync(receipt, token);
            await preparation.InspectAsync(root, receipt.Plan.Current, ReleaseStore.Select(root, receipt.Plan.Current.Release!), receipt.Plan.Operation, token);
            if (!await new ReleaseImagesVerifier(runner).VerifyAsync(target, token)) throw new IOException("Target image unavailable.");
            var name = receipt.Plan.Current.Project + "-update-migrate-" + receipt.Plan.Operation.ToString("N");
            receipt = receipt with { MigrationContainer = name };
            receipt.Save(root);
            var id = (await owner.Required(["create", "--name", name, "--label", "wayfarer.update=" + receipt.Plan.Operation.ToString("D"),
                "--restart=no", "--pull=never", .. UpdatePreparation.Maintenance(root, receipt.Plan.Target), "--entrypoint=dotnet",
                "ghcr.io/stef-k/wayfarer@" + receipt.Plan.Target.AppDigest, "Wayfarer.dll", "database", "migrate"], token)).Trim();
            receipt = (receipt with { MigrationContainerId = id }).Advance(UpdatePhase.MigrationStarted);
            receipt.Save(root);
            await owner.Required(["start", id], token);
            await owner.Required(["wait", id], token);
        }
        if (receipt.Phase != UpdatePhase.MigrationStarted) return receipt;
        using var inspection = JsonDocument.Parse(await owner.Required(["inspect", receipt.MigrationContainer!], token));
        var helper = inspection.RootElement[0];
        if (helper.GetProperty("Id").GetString() != receipt.MigrationContainerId ||
            helper.GetProperty("Config").GetProperty("Labels").GetProperty("wayfarer.update").GetString() != receipt.Plan.Operation.ToString("D") ||
            helper.GetProperty("Config").GetProperty("Image").GetString() != "ghcr.io/stef-k/wayfarer@" + receipt.Plan.Target.AppDigest ||
            helper.GetProperty("State").GetProperty("Running").GetBoolean())
            throw new IOException("Migration helper unresolved; wait for exact helper termination before resume.");
        receipt = receipt with { MigrationExit = helper.GetProperty("State").GetProperty("ExitCode").GetInt32() };
        receipt.Save(root);
        // Independent target inspection, not exit code, proves complete EF/Quartz/secure readiness.
        var evidence = await preparation.InspectAsync(root, receipt.Plan.Target, target, receipt.Plan.Operation, token);
        receipt = (receipt with { Reconciliation = evidence }).Advance(UpdatePhase.MigrationConfirmed);
        receipt.Save(root);
        return receipt;
    }
}
