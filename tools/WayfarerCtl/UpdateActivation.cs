using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Same-generation target activation with a single release pointer and private postflight.</summary>
public sealed class UpdateActivation(IProcessRunner runner)
{
    /// <summary>Persist target backup and deployment inputs before the single atomic installation rename.</summary>
    public async Task<UpdateReceipt> ActivateAsync(string root, UpdateReceipt receipt, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        var directory = UpdatePreparation.DirectoryFor(root, receipt.Plan.Operation);
        var target = receipt.Plan.Target;
        if (receipt.Phase == UpdatePhase.MigrationConfirmed)
        {
            var bundle = ReleaseStore.Select(root, target.Release!);
            using var facts = JsonDocument.Parse(receipt.Reconciliation!);
            var source = bundle.Target(target.Project, true) with
            { QuartzSnapshotFingerprint = facts.RootElement.GetProperty("QuartzSnapshotFingerprint").GetString()! };
            var backup = target.Backup! with { Generation = receipt.PlanHash,
                Payload = Path.Combine(bundle.Directory, "wayfarer-recovery"), PayloadSha256 = source.PayloadFingerprint, Source = source };
            target = target with { Backup = backup };
            receipt = (receipt with { NewConfiguration = JsonSerializer.Serialize(target) }).Advance(UpdatePhase.ActivationIntent);
            receipt.Save(root);
        }
        target = JsonSerializer.Deserialize<Deployment>(receipt.NewConfiguration!, ArchiveContract.Json)!;
        StageInputs(root, target);
        var current = File.ReadAllText(Path.Combine(root, "installation.json"));
        if (current != receipt.Plan.OldConfiguration && current != receipt.NewConfiguration)
            throw new IOException("Installation pointer outside update transaction.");
        if (receipt.Phase == UpdatePhase.ActivationIntent)
        {
            Commit(root, receipt.NewConfiguration!);
            var transition = Overlay(directory, target, false);
            await owner.Required(target.Compose(root, "-f", transition, "create", "--force-recreate", "--pull", "never", "db", "wayfarer"), token);
            receipt = await RecordServicesAsync(root, receipt, target, token);
            await VerifyPrivateAsync(root, target, token);
            receipt = receipt.Advance(UpdatePhase.TargetActivatedStopped);
            receipt.Save(root);
        }
        var privateOverlay = Overlay(directory, target, false);
        if (receipt.Phase == UpdatePhase.TargetActivatedStopped)
        {
            await owner.Required(target.Compose(root, "-f", privateOverlay, "up", "-d", "--no-recreate", "--pull", "never", "--wait", "--wait-timeout", "180", "db"), token);
            var evidence = await new UpdatePreparation(runner).InspectAsync(root, target,
                ReleaseStore.Select(root, target.Release!), receipt.Plan.Operation, token);
            receipt = (receipt with { Reconciliation = evidence }).Advance(UpdatePhase.WritesPossible);
            receipt.Save(root);
        }
        if (receipt.Phase == UpdatePhase.WritesPossible)
        {
            await owner.Required(target.Compose(root, "-f", privateOverlay, "up", "-d", "--no-recreate", "--pull", "never", "--wait", "--wait-timeout", "180", "wayfarer"), token);
            await owner.Required(target.Compose(root, "exec", "-T", "wayfarer", "dotnet", "Wayfarer.dll", "healthcheck"), token);
            var evidence = await new UpdatePreparation(runner).InspectAsync(root, target,
                ReleaseStore.Select(root, target.Release!), receipt.Plan.Operation, token);
            receipt = (receipt with { Reconciliation = evidence }).Advance(UpdatePhase.PostflightConfirmed);
            receipt.Save(root);
        }
        if (receipt.Phase == UpdatePhase.PostflightConfirmed)
        {
            var exposure = Overlay(directory, target, true);
            await owner.Required(target.Compose(root, "-f", exposure, "up", "-d", "--force-recreate", "--pull", "never", "--wait", "--wait-timeout", "180", "wayfarer"), token);
            if (target.Mode == "managed")
                await owner.Required(target.Compose(root, "-f", exposure, "up", "-d", "--pull", "never", "caddy"), token);
            await Diagnostics.EndpointAsync(target, token, ReleaseStore.Select(root, target.Release!).Manifest.Version);
            BackupCompose.Check(root, target);
            await owner.Required(BackupCompose.Command(root, target, "up", "-d", "--force-recreate", "--pull", "never", "backup-scheduler"), token);
            receipt = await RecordServicesAsync(root, receipt, target, token);
            foreach (var id in receipt.Containers)
            {
                using var document = JsonDocument.Parse(await owner.Required(["inspect", id], token));
                var service = document.RootElement[0].GetProperty("Config").GetProperty("Labels")
                    .GetProperty("com.docker.compose.service").GetString()!;
                if (service is not ("db" or "wayfarer" or "caddy" or "backup-scheduler")) continue;
                var policy = receipt.ServiceRestartPolicies.GetValueOrDefault(service, "unless-stopped");
                await owner.Required(["update", "--restart=" + policy, id], token);
            }
            var completion = Path.Combine(directory, "completed");
            if (!File.Exists(completion)) ProtectedFiles.Create(completion, receipt.PlanHash);
            using var parent = new SafeDirectory(directory);
            parent.Flush();
            receipt = receipt.Advance(UpdatePhase.Accepted);
            receipt.Save(root);
        }
        return receipt;
    }

    /// <summary>Immutable prepared inputs allow crash-safe activation without rewriting a running operator or legacy env file.</summary>
    private static void StageInputs(string root, Deployment target)
    {
        var directory = Path.Combine(root, "deployment-generations", target.Release!.Fingerprint);
        Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
        var path = Path.Combine(directory, "deployment.env");
        if (!File.Exists(path)) ProtectedFiles.Create(path, target.EnvironmentFile(root));
        ProtectedFiles.Check(path, 0);
        if (File.ReadAllText(path) != target.EnvironmentFile(root)) throw new IOException("Target inputs changed.");
        using var parent = new SafeDirectory(directory);
        parent.Flush();
        using var generations = new SafeDirectory(Path.GetDirectoryName(directory)!);
        generations.Flush();
        var backup = BackupCompose.DirectoryPath(root, target.Backup!);
        if (!Directory.Exists(backup)) BackupGeneration.Stage(root, target);
        BackupCompose.Check(root, target);
    }

    /// <summary>Single durable pointer commit preserves physical storage, secrets and proxy choices.</summary>
    internal static void Commit(string root, string configuration)
    {
        var temporary = Path.Combine(root, "installation.update-" + Guid.NewGuid().ToString("N"));
        ProtectedFiles.Create(temporary, configuration);
        File.Move(temporary, Path.Combine(root, "installation.json"), true);
        using var parent = new SafeDirectory(root);
        parent.Flush();
    }

    private static string Overlay(string directory, Deployment config, bool expose)
    {
        var path = Path.Combine(directory, expose ? "exposure.yaml" : "transition.yaml");
        var content = RestoreActivation.Transition(config, expose);
        if (!File.Exists(path)) ProtectedFiles.Create(path, content);
        if (File.ReadAllText(path) != content) throw new IOException("Update transition changed.");
        return path;
    }

    /// <summary>Replace old container IDs only after binding the newly created service identities.</summary>
    private async Task<UpdateReceipt> RecordServicesAsync(string root, UpdateReceipt receipt, Deployment config, CancellationToken token)
    {
        var output = await new RestoreContainers(runner).Required(["ps", "-aq", "--no-trunc", "--filter", "label=com.docker.compose.project=" + config.Project], token);
        receipt = receipt with { Containers = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) };
        receipt.Save(root);
        return receipt;
    }

    /// <summary>No target normal writer may start with ports or a different durable mapping.</summary>
    private async Task VerifyPrivateAsync(string root, Deployment target, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        foreach (var service in new[] { "db", "wayfarer" })
        {
            var id = (await owner.Required(target.Compose(root, "ps", "-aq", service), token)).Trim();
            using var document = JsonDocument.Parse(await owner.Required(["inspect", id], token));
            var value = document.RootElement[0];
            UpdateRuntime.VerifyService(root, target, service, value);
            if (value.GetProperty("State").GetProperty("Running").GetBoolean() ||
                value.GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString() != "no" ||
                value.GetProperty("Config").GetProperty("Image").GetString() != (service == "db" ?
                    "ghcr.io/stef-k/wayfarer-db@" + target.DbDigest : "ghcr.io/stef-k/wayfarer@" + target.AppDigest))
                throw new IOException("Unexpected target service authority.");
            var ports = value.GetProperty("HostConfig").GetProperty("PortBindings");
            if (ports.ValueKind == JsonValueKind.Object && ports.EnumerateObject().Any()) throw new IOException("Target exposed before private postflight.");
            foreach (var (role, destination) in service == "db" ? new[] { ("db-data", "/var/lib/postgresql/data") } :
                new[] { ("app-data", "/var/lib/wayfarer"), ("app-cache", "/var/cache/wayfarer") })
                if (!value.GetProperty("Mounts").EnumerateArray().Any(mount => mount.GetProperty("Type").GetString() == "volume" &&
                    mount.GetProperty("Name").GetString() == ActiveStorage.Volume(target, role) && mount.GetProperty("Destination").GetString() == destination))
                    throw new IOException("Target changed active storage mapping.");
        }
    }
}
