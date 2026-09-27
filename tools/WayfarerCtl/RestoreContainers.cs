namespace WayfarerCtl;

/// <summary>Named helper ownership survives Docker client cancellation and uncertain daemon acknowledgements.</summary>
public sealed class RestoreContainers(IProcessRunner runner)
{
    /// <summary>Wait for the actual container; on failure stop/wait it independently before reporting cleanup.</summary>
    public async Task<string> RunAsync(string name, string[] create, CancellationToken token)
    {
        try
        {
            await Required(["create", "--name", name, "--restart=no", "--pull=never", .. create], token);
            await Required(["start", name], token);
            var exit = await Required(["wait", name], token);
            if (exit.Trim() != "0") throw new IOException("Restore helper failed.");
            return await Required(["logs", "--tail", "1", name], token);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            // Preserve container evidence. A failed cleanup never authorizes continuation or pointer rollback.
            await Required(["stop", "--time", "20", name], cleanup.Token);
            await Required(["wait", name], cleanup.Token);
        }
    }

    /// <summary>Fixed hardening shared by parsers and SQL helpers; only the caller adds necessary mounts/network.</summary>
    public static string[] Unprivileged() => ["--platform=linux/amd64", "--user=1654:1654", "--read-only",
        "--cap-drop=ALL", "--security-opt=no-new-privileges:true", "--init", "--cpus=1", "--memory=512m",
        "--pids-limit=64", "--tmpfs=/tmp:uid=1654,gid=1654,mode=0700,size=67108864"];

    public async Task<string> Required(string[] command, CancellationToken token)
    {
        var result = await runner.RunAsync(command, null, token);
        if (result.Code != 0) throw new IOException("Restore Docker operation failed; resource state may be uncertain.");
        return result.Output;
    }
}
