using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>One installation pointer commits paired storage while transition composition fences restart and ingress.</summary>
public sealed class RestoreActivation(IProcessRunner runner)
{
    /// <summary>Fixed YAML tags remove external ports until private postflight, overriding only trusted services.</summary>
    public static string Transition(Deployment config, bool expose) => "services:\n  db:\n    restart: 'no'\n  wayfarer:\n    restart: 'no'\n" +
        (expose ? "" : "    ports: !reset []\n") + (config.Mode == "managed" ? "  caddy:\n    restart: 'no'\n" : "");

    /// <summary>Commit configuration with an immutable prewritten overlay and flush the pointer's parent directory.</summary>
    public static void Commit(string root, Deployment candidate)
    {
        var overlay = ActiveStorage.OverlayPath(root, candidate);
        Directory.CreateDirectory(Path.GetDirectoryName(overlay)!, ProtectedFiles.PrivateDirectory);
        if (!File.Exists(overlay)) ProtectedFiles.Create(overlay, ActiveStorage.Render(candidate));
        ActiveStorage.Check(root, candidate);
        using (var directory = new SafeDirectory(Path.GetDirectoryName(overlay)!)) directory.Flush();
        var temporary = Path.Combine(root, "installation.json." + Guid.NewGuid().ToString("N"));
        ProtectedFiles.Create(temporary, JsonSerializer.Serialize(candidate));
        File.Move(temporary, Path.Combine(root, "installation.json"), overwrite: true);
        using var parent = new SafeDirectory(root);
        parent.Flush();
    }

    /// <summary>Recreate canonical services with restart disabled and no public exposure before the writer cutoff.</summary>
    public async Task<RestoreReceipt> ActivateAsync(string root, RestoreReceipt receipt, CancellationToken token)
    {
        var candidate = RestoreCandidate.Configuration(receipt.EffectivePlan);
        var directory = RestorePreparation.DirectoryFor(root, receipt.Plan.Operation);
        var transition = Path.Combine(directory, "transition.yaml");
        var owner = new RestoreContainers(runner);
        if (receipt.Phase == RestorePhase.CandidateValidated)
        {
            receipt = receipt with { OldConfiguration = File.ReadAllText(Path.Combine(root, "installation.json")),
                NewConfiguration = JsonSerializer.Serialize(candidate) };
            receipt = receipt.Advance(RestorePhase.ActivationIntent);
            receipt.Save(root);
        }
        var current = File.ReadAllText(Path.Combine(root, "installation.json"));
        if (current != receipt.OldConfiguration && current != receipt.NewConfiguration)
            throw new IOException("Installation pointer is outside the receipted transaction.");
        if (receipt.Phase > RestorePhase.ActivationIntent && current != receipt.NewConfiguration)
            throw new IOException("Activated installation pointer changed.");
        if (!File.Exists(transition)) ProtectedFiles.Create(transition, Transition(candidate, false));
        if (File.ReadAllText(transition) != Transition(candidate, false)) throw new IOException("Transition configuration changed.");
        Commit(root, candidate);
        string[] Compose(params string[] args) => candidate.Compose(root, ["-f", transition, .. args]);
        await owner.Required(Compose("config", "--quiet"), token);
        if (receipt.Plan.NewInstall)
        {
            await owner.Required(["volume", "create", "--label", "com.docker.compose.project=" + candidate.Project,
                "--label", "com.docker.compose.volume=app-logs", ActiveStorage.Volume(candidate, "app-logs")], token);
            await owner.RunAsync(candidate.Project + "-restore-logs-" + Guid.NewGuid().ToString("N"),
                ["--network=none", "--read-only", "--user=0", "--cap-drop=ALL", "--cap-add=CHOWN", "--cap-add=FOWNER",
                    "--volume", ActiveStorage.Volume(candidate, "app-logs") + ":/logs", "--entrypoint=sh",
                    "ghcr.io/stef-k/wayfarer@" + candidate.AppDigest, "-ec", "chown 1654:1654 /logs; chmod 750 /logs"], token);
        }
        receipt = receipt with { Containers = receipt.Containers.Concat(new[] { candidate.Project + "-db-1",
            candidate.Project + "-wayfarer-1", candidate.Project + "-caddy-1", candidate.Project + "-backup-scheduler-1" }).Distinct().ToArray() };
        receipt.Save(root);
        await owner.Required(Compose("create", "--force-recreate", "--pull", "never", "db", "wayfarer"), token);
        await VerifyMountsAsync(root, candidate, transition, token);
        if (receipt.Phase == RestorePhase.ActivationIntent)
        {
            receipt = receipt.Advance(RestorePhase.ActivatedStopped);
            receipt.Save(root);
        }
        await owner.Required(Compose("up", "-d", "--no-recreate", "--pull", "never", "--wait", "--wait-timeout", "180", "db"), token);
        await new RestoreCandidate(runner).InspectAsync(root, candidate, directory, candidate.Project + "_backend",
            candidate.Project + "-restore-canonical-" + Guid.NewGuid().ToString("N"), token);
        if (receipt.Phase == RestorePhase.ActivatedStopped)
        {
            receipt = receipt.Advance(RestorePhase.WritesPossible);
            receipt.Save(root);
        }
        await owner.Required(Compose("up", "-d", "--no-recreate", "--pull", "never", "--wait", "--wait-timeout", "180", "wayfarer"), token);
        await owner.Required(Compose("exec", "-T", "wayfarer", "dotnet", "Wayfarer.dll", "healthcheck"), token);
        var exposed = Path.Combine(directory, "exposure.yaml");
        if (!File.Exists(exposed)) ProtectedFiles.Create(exposed, Transition(candidate, true));
        if (File.ReadAllText(exposed) != Transition(candidate, true)) throw new IOException("Exposure configuration changed.");
        await owner.Required(candidate.Compose(root, "-f", exposed, "up", "-d", "--force-recreate", "--pull", "never",
            "--wait", "--wait-timeout", "180", "wayfarer"), token);
        if (candidate.Mode == "managed")
            await owner.Required(candidate.Compose(root, "-f", exposed, "up", "-d", "--pull", "never", "caddy"), token);
        var source = JsonSerializer.Deserialize<SourceIdentity>(File.ReadAllText(Path.Combine(directory, "source.json")))!;
        await Diagnostics.EndpointAsync(candidate, token, source.ApplicationVersion);
        if (candidate.Backup is { Enabled: true })
            await owner.Required(BackupCompose.Command(root, candidate, "up", "-d", "--force-recreate", "--pull", "never", "backup-scheduler"), token);
        // All required postflight gates passed before normal restart policy is enabled.
        receipt = receipt.Advance(RestorePhase.Accepted);
        receipt.Save(root);
        var active = (await owner.Required(candidate.Compose(root, "ps", "-q"), token)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var id in active) await owner.Required(["update", "--restart=unless-stopped", id], token);
        InstallationCompletion.RecordRestore(root, receipt);
        return receipt;
    }
    /// <summary>Inspect actual created mounts/images/restart policies before any canonical candidate writer can launch.</summary>
    private async Task VerifyMountsAsync(string root, Deployment candidate, string transition, CancellationToken token)
    {
        var owner = new RestoreContainers(runner);
        foreach (var service in new[] { "db", "wayfarer" })
        {
            var id = (await owner.Required(candidate.Compose(root, "-f", transition, "ps", "-aq", service), token)).Trim();
            using var document = JsonDocument.Parse(await owner.Required(["inspect", id], token));
            var container = document.RootElement[0];
            var expectedImage = service == "db" ? "ghcr.io/stef-k/wayfarer-db@" + candidate.DbDigest : "ghcr.io/stef-k/wayfarer@" + candidate.AppDigest;
            if (container.GetProperty("Config").GetProperty("Image").GetString() != expectedImage ||
                container.GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString() != "no" ||
                container.GetProperty("State").GetProperty("Running").GetBoolean()) throw new IOException("Unexpected canonical candidate state.");
            var targets = service == "db" ? new[] { ("db-data", "/var/lib/postgresql/data") } :
                new[] { ("app-data", "/var/lib/wayfarer"), ("app-cache", "/var/cache/wayfarer") };
            foreach (var (role, target) in targets)
                if (!container.GetProperty("Mounts").EnumerateArray().Any(mount => mount.GetProperty("Type").GetString() == "volume" &&
                    mount.GetProperty("Name").GetString() == ActiveStorage.Volume(candidate, role) && mount.GetProperty("Destination").GetString() == target))
                    throw new IOException("Canonical candidate mount mismatch.");
            var ports = container.GetProperty("HostConfig").GetProperty("PortBindings");
            if (ports.ValueKind == JsonValueKind.Object && ports.EnumerateObject().Any()) throw new IOException("Candidate ingress exposed before postflight.");
        }
    }

}
