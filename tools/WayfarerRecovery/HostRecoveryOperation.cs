using System.Text.Json;

namespace WayfarerRecovery;

/// <summary>Host-created lifecycle evidence, protected by recovery exclusion; never a public skip-lock flag.</summary>
public sealed record HostRecoveryOperation(int Schema, string Token, string Container, bool Quiesced, bool RestoreHold = false)
{
    /// <summary>Only the exact delegated host reservation may request a permanent emergency retention hold.</summary>
    public static bool RequiresHold(string? token)
    {
        if (token is null) return false;
        Validate(token);
        return JsonSerializer.Deserialize<HostRecoveryOperation>(File.ReadAllText("/control/host-operation.json"), ArchiveContract.Json)!.RestoreHold;
    }

    /// <summary>Scheduler defers while a host operation is reserved; manual workers must match the exact receipt.</summary>
    public static bool Validate(string? token)
    {
        const string path = "/control/host-operation.json";
        if (!File.Exists(path))
        {
            if (token is not null || File.Exists("/control/restore-in-progress")) throw new IOException("Host reservation missing or restore unresolved.");
            return false;
        }
        if (new FileInfo(path).Length > 2048) throw new IOException("Invalid host reservation.");
        var receipt = JsonSerializer.Deserialize<HostRecoveryOperation>(File.ReadAllText(path), ArchiveContract.Json);
        if (token is null || receipt is null || receipt.Schema != 1 || receipt.Token != token ||
            File.Exists("/control/restore-in-progress") && !receipt.RestoreHold)
            throw new IOException("Recovery reserved by host.");
        return receipt.Quiesced;
    }
}
