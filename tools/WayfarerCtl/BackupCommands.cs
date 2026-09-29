using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Owns actual maintenance containers through completion/cancellation, not merely Docker client processes.</summary>
public sealed class BackupCommands(IProcessRunner runner, ITerminal terminal)
{
    public Guid? CompletedArchive { get; private set; }

    /// <summary>Validate capability, reserve lifecycle intent under the shared lock and invoke the same worker.</summary>
    public async Task<int> RunAsync(string root, Deployment config, string[] args, CancellationToken token, bool restoreEmergency = false, bool updateRecovery = false)
    {
        if (!updateRecovery && !restoreEmergency) UpdateReceipt.RequireResolved(root);
        if (args is ["backup", "configure", ..])
        {
            var next = await new BackupConfiguration(runner).ConfigureAsync(root, config, args[2..], token);
            if (next.Backup?.Enabled == true) await Required(BackupCompose.Command(root, next, "up", "-d", "--no-deps", "--pull", "never", "backup-scheduler"), token);
            terminal.Write(next.Backup?.Enabled == true ? "Backup configured; scheduler enabled." : "Backup disabled; scheduler stopped.");
            return 0;
        }
        if (File.Exists(Path.Combine(root, "backup-transition.json"))) throw new UsageException("Interrupted backup configuration; run backup configure --recover.");
        var policy = config.Backup ?? throw new UsageException("Backup is not configured.");
        BackupCompose.Check(root, config);
        if (!policy.Enabled && args[0] == "backup") throw new UsageException("Backup disabled.");
        var control = Path.Combine(root, "recovery-control");
        var container = config.Project + "-backup-" + Guid.NewGuid().ToString("N");
        var operation = args[0] == "verify-backup" ? "verify" : args[0];
        var quiesced = args is ["backup", "--quiesced"];
        var reservation = Guid.NewGuid().ToString("N");
        using (var exclusion = new RecoveryLock(Path.Combine(control, "recovery.lock")))
        {
            if (File.Exists(Path.Combine(control, "host-operation.json"))) throw new IOException("Unresolved recovery operation.");
            if (quiesced)
            {
                await Required(BackupCompose.Command(root, config, "stop", "--timeout", "30", "backup-scheduler"), token);
                await Required(config.Compose(root, "stop", "--timeout", "70", "wayfarer"), token);
                await AssertNoWriters(config, token);
            }
            if (restoreEmergency)
            {
                var restore = RestoreReceipt.Load(root) ?? throw new IOException("Restore delegation requires durable intent.");
                (restore with { Containers = [.. restore.Containers, container] }).Save(root);
            }
            if (updateRecovery)
            {
                var update = UpdateReceipt.Load(root) ?? throw new IOException("Update delegation requires intent.");
                if (update.Phase is not (UpdatePhase.Fenced or UpdatePhase.RecoveryVerified) || !quiesced) throw new IOException("Invalid update capture phase.");
            }
            ProtectedFiles.Create(Path.Combine(control, "host-operation.json"), JsonSerializer.Serialize(new HostRecoveryOperation(1, reservation, container, quiesced, restoreEmergency, updateRecovery)), 0, 1654);
        }
        var stopped = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(policy.DeadlineSeconds + 30));
        try
        {
            var workerArgs = new List<string> { operation, "--host-operation", reservation };
            if (operation == "verify" && args.Length == 2) workerArgs.Add(args[1]);
            await Required(BackupCompose.Command(root, config, ["run", "-d", "--no-deps", "--name", container, BackupCompose.ServiceFor(operation), .. workerArgs]), deadline.Token);
            var wait = await runner.RunAsync(["wait", container], null, deadline.Token);
            if (wait.Code != 0 || !int.TryParse(wait.Output.Trim(), out var code)) throw new IOException("Worker state unknown.");
            stopped = true;
            var logs = await runner.RunAsync(["logs", "--tail", "1", container], null, deadline.Token);
            if (logs.Code != 0 || logs.Output.Length > 32768) throw new IOException("Worker result unavailable.");
            using var result = JsonDocument.Parse(logs.Output);
            Present(result.RootElement);
            if (code == 0 && result.RootElement.TryGetProperty("Archive", out var archive)) CompletedArchive = archive.GetGuid();
            return code == 0 ? 0 : 1;
        }
        finally
        {
            // Independent cleanup deadline survives Ctrl-C; a lost daemon must never be called successful cancellation.
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                if (!stopped)
                {
                    var stop = await runner.RunAsync(["stop", "--time", "30", container], null, cleanup.Token);
                    var wait = await runner.RunAsync(["wait", container], null, cleanup.Token);
                    stopped = stop.Code == 0 && wait.Code == 0;
                }
                if (stopped)
                {
                    await runner.RunAsync(["rm", container], null, cleanup.Token);
                    using var exclusion = new RecoveryLock(Path.Combine(control, "recovery.lock"));
                    File.Delete(Path.Combine(control, "host-operation.json"));
                }
                else terminal.Error("Worker state unknown; recovery reservation retained. Inspect the owned container before recovery.");
            }
            catch (Exception) { terminal.Error("Worker cleanup state unknown; recovery reservation retained. Prior operation outcome remains authoritative."); }
            if (quiesced) terminal.Write("Application remains stopped after deliberate quiesced capture; use start when the protected transition is finished.");
        }
    }

    /// <summary>No running container may share the durable application volume during quiesced capture.</summary>
    private async Task AssertNoWriters(Deployment config, CancellationToken token)
    {
        var result = await runner.RunAsync(["ps", "-q", "--filter", "volume=" + ActiveStorage.Volume(config, "app-data")], null, token);
        if (result.Code != 0 || !string.IsNullOrWhiteSpace(result.Output)) throw new IOException("Application volume still has an active consumer.");
    }

    /// <summary>Print only generated UUID/name/state fields, never raw manifest or child diagnostic strings.</summary>
    private void Present(JsonElement result)
    {
        if (result.GetProperty("Schema").GetInt32() != 1) throw new IOException("Unsupported worker result.");
        if (result.TryGetProperty("Failure", out _)) { terminal.Error("Recovery failed; source/destination/lock or archive validation did not pass."); return; }
        if (result.TryGetProperty("Archives", out var archives))
        {
            foreach (var row in archives.EnumerateArray())
            {
                var name = row.GetProperty("Name").GetString()!;
                WayfarerRecovery.SafeDirectory.ValidateName(name);
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^wayfarer-recovery-v1_[a-f0-9-]{36}_[0-9]{8}T[0-9]{13}Z_[a-f0-9-]{36}\\.tar$")) throw new IOException("Invalid worker archive identity.");
                terminal.Write(name + " (listing only; not fully verified)");
            }
            if (result.GetProperty("More").GetBoolean()) terminal.Write("Additional archives omitted by the listing cap.");
            return;
        }
        terminal.Write("Archive " + result.GetProperty("Archive").GetGuid());
        if (result.TryGetProperty("IntegrityValid", out var integrity))
            terminal.Write($"Integrity: {integrity.GetBoolean()}; compatible: {result.GetProperty("CompatibilitySupported").GetBoolean()}");
        if (result.TryGetProperty("RetentionSucceeded", out var retention)) terminal.Write("Retention succeeded: " + retention.GetBoolean());
    }

    private async Task Required(string[] arguments, CancellationToken token)
    {
        if ((await runner.RunAsync(arguments, null, token)).Code != 0) throw new IOException("Recovery container operation failed.");
    }
}
