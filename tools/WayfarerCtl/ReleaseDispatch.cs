using System.Diagnostics;
using System.Security.Cryptography;

namespace WayfarerCtl;

/// <summary>Explicit stable bootstrap dispatch: select validated bytes, then invoke only the fixed retained operator filename.</summary>
public static class ReleaseDispatch
{
    /// <summary>Receipt recovery selects the exact original owner even when ordinary installation selection changes.</summary>
    public static ReleaseBundle Select(string root, bool resume)
    {
        ReleaseAuthority owner;
        if (resume)
            owner = RestoreReceipt.Load(root)?.Plan.OperatorOwner ?? throw new UsageException("Receipt has no retained operator owner; invoke its original operator explicitly.");
        else
            owner = Deployment.Load(root).Release ?? throw new UsageException("Installation has not adopted a local release.");
        var bundle = ReleaseStore.Select(root, owner);
        ReleaseContract.RequireUse(bundle.Manifest, bundle.Manifest.Operator.Version);
        return bundle;
    }

    /// <summary>The stable executable is never replaced; retained children receive literal argv and inherited terminal streams.</summary>
    public static async Task<int> RunAsync(string root, string[] args, CancellationToken token)
    {
        if (args.Length == 0 || args[0] == "dispatch") throw new UsageException("dispatch requires one ordinary command.");
        var resume = args is ["restore", "--resume" or "--abort", ..];
        var selected = args is ["update", "--resume" or "--abort" or "--restore", ..]
            ? ReleaseStore.Select(root, (UpdateReceipt.Load(root) ?? throw new UsageException("Missing update owner.")).Plan.OperatorOwner)
            : Select(root, resume);
        var executable = Path.Combine(selected.Directory, "wayfarerctl");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--deployment-root");
        start.ArgumentList.Add(root);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.ToArray())
            if (key.StartsWith("DOTNET_", StringComparison.Ordinal) || key.StartsWith("CORECLR_", StringComparison.Ordinal) ||
                key.StartsWith("LD_", StringComparison.Ordinal)) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new IOException("Retained operator did not start.");
        // Cancellation of the bootstrap does not kill an operation owner during a durable transition.
        await process.WaitForExitAsync(token);
        return process.ExitCode;
    }

    /// <summary>Pin the running executable to the selected release before ordinary mutation or new restore authorization.</summary>
    public static ReleaseAuthority? CurrentOwner(string root, Deployment config) => CurrentOwner(root, config, null);

    /// <summary>Use the same retained authority for hosted tests with a protected executable fixture.</summary>
    internal static ReleaseAuthority? CurrentOwner(string root, Deployment config, string? executable)
    {
        if (config.Release is null) return null;
        var bundle = ReleaseStore.Select(root, config.Release);
        RequireExecutable(bundle, executable);
        return ReleaseAuthority.From(bundle);
    }

    /// <summary>Bind direct execution as well as dispatch to the retained executable's actual bytes.</summary>
    public static void RequireExecutable(ReleaseBundle bundle) => RequireExecutable(bundle, null);

    /// <summary>Validate the same protected path and SHA-256 contract for native execution and hosted executable fixtures.</summary>
    internal static void RequireExecutable(ReleaseBundle bundle, string? executable)
    {
        executable ??= Environment.ProcessPath ?? throw new IOException("Current operator path unavailable.");
        ProtectedFiles.SafePath(executable);
        using var file = File.OpenRead(executable);
        if (Convert.ToHexStringLower(SHA256.HashData(file)) != ReleaseAuthority.From(bundle).OperatorSha256)
            throw new IOException("Invoke the exact retained release operator through dispatch.");
    }

    /// <summary>New receipts cannot be resumed by an unrelated executable, even through a direct invocation.</summary>
    public static void RequireOwner(string root, RestorePlan plan)
    {
        if (plan.OperatorOwner is not { } owner) return; // Existing receipts retain their explicit recovery path.
        var bundle = ReleaseStore.Select(root, owner);
        if (!bundle.Manifest.Operator.RestoreReceiptSchemas.Contains(1)) throw new IOException("Unsupported receipt owner.");
        RequireExecutable(bundle);
    }
}
