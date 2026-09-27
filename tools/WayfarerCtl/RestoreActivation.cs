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
        var candidate = RestoreCandidate.Configuration(receipt.Plan);
        var directory = RestorePreparation.DirectoryFor(root, receipt.Plan.Operation);
        var transition = Path.Combine(directory, "transition.yaml");
        var owner = new RestoreContainers(runner);
        receipt = receipt with { OldConfiguration = File.ReadAllText(Path.Combine(root, "installation.json")),
            NewConfiguration = JsonSerializer.Serialize(candidate) };
        receipt = receipt.Advance(RestorePhase.ActivationIntent);
        receipt.Save(root);
        ProtectedFiles.Create(transition, Transition(candidate, false));
        Commit(root, candidate);
        string[] Compose(params string[] args) => candidate.Compose(root, ["-f", transition, .. args]);
        await owner.Required(Compose("config", "--quiet"), token);
        await owner.Required(Compose("create", "--force-recreate", "--pull", "never", "db", "wayfarer"), token);
        var ids = (await owner.Required(Compose("ps", "-aq"), token)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        receipt = receipt with { Containers = [.. receipt.Containers, .. ids] };
        receipt = receipt.Advance(RestorePhase.ActivatedStopped);
        receipt.Save(root);
        await owner.Required(Compose("up", "-d", "--no-recreate", "--pull", "never", "--wait", "--wait-timeout", "180", "db"), token);
        await new RestoreCandidate(runner).InspectAsync(root, candidate, directory, candidate.Project + "_backend",
            candidate.Project + "-restore-canonical-" + receipt.Plan.Operation.ToString("N"), token);
        receipt = receipt.Advance(RestorePhase.WritesPossible);
        receipt.Save(root);
        await owner.Required(Compose("up", "-d", "--no-recreate", "--pull", "never", "--wait", "--wait-timeout", "180", "wayfarer"), token);
        await owner.Required(Compose("exec", "-T", "wayfarer", "dotnet", "Wayfarer.dll", "healthcheck"), token);
        var exposed = Path.Combine(directory, "exposure.yaml");
        ProtectedFiles.Create(exposed, Transition(candidate, true));
        await owner.Required(candidate.Compose(root, "-f", exposed, "up", "-d", "--force-recreate", "--pull", "never",
            "--wait", "--wait-timeout", "180", "wayfarer"), token);
        if (candidate.Mode == "managed")
            await owner.Required(candidate.Compose(root, "-f", exposed, "up", "-d", "--pull", "never", "caddy"), token);
        var source = JsonSerializer.Deserialize<SourceIdentity>(File.ReadAllText(Path.Combine(directory, "source.json")))!;
        await Diagnostics.EndpointAsync(candidate, token, source.ApplicationVersion);
        if (candidate.Backup is { Enabled: true })
            await owner.Required(BackupCompose.Command(root, candidate, "up", "-d", "--force-recreate", "--pull", "never", "backup-scheduler"), token);
        // All required postflight gates passed before normal restart policy is enabled.
        var active = (await owner.Required(candidate.Compose(root, "ps", "-q"), token)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var id in active) await owner.Required(["update", "--restart=unless-stopped", id], token);
        receipt = receipt.Advance(RestorePhase.Accepted);
        receipt.Save(root);
        return receipt;
    }
}
