namespace WayfarerCtl;

/// <summary>Ordinary retained start/stop/restart behavior is also the healthy-runtime seam for deliberate reactivation.</summary>
internal sealed class DeploymentLifecycle(IProcessRunner runner, ITerminal terminal)
{
    /// <summary>Use retained Compose inputs without pull or migration; the caller owns host and recovery exclusion.</summary>
    internal async Task<int> RunAsync(string root, Deployment config, string operation, CancellationToken token)
    {
        if (operation is "stop" or "restart" && config.Backup is not null)
            await Required(BackupCompose.Command(root, config, "stop", "--timeout", "30", "backup-scheduler"), token);
        if (operation is "stop" or "restart")
            await Required(config.Compose(root, "stop", "--timeout", "70"), token);
        if (operation != "stop")
        {
            await Required(config.Compose(root, "up", "-d", "--no-recreate", "--pull", "never", "--wait", "--wait-timeout", "180"), token);
            if (config.Backup is { Enabled: true })
                await Required(BackupCompose.Command(root, config, "up", "-d", "--no-deps", "--pull", "never", "backup-scheduler"), token);
            return await new Diagnostics(runner, terminal).RunAsync(root, config, true, token);
        }
        terminal.Write("Stopped. All durable volumes retained.");
        return 0;
    }

    /// <summary>Child diagnostics remain private; client success is followed by ordinary live diagnostics on start.</summary>
    private async Task Required(string[] arguments, CancellationToken token)
    {
        if ((await runner.RunAsync(arguments, null, token)).Code != 0) throw new IOException("Docker operation failed.");
    }
}
