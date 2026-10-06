using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Terminal replay and dispatch retain only an operation, accepted hash and exact immutable operator owner.</summary>
public sealed record UninstallPurgeTombstone(int Schema, Guid Operation, string PlanHash, ReleaseAuthority OperatorOwner, string Result)
{
    /// <summary>One fixed protected terminal filename never impersonates installation configuration.</summary>
    internal const string Name = "uninstall-purged.json";

    /// <summary>Derive terminal evidence only from complete purge absence progress; the full terminal receipt is never saved.</summary>
    internal static UninstallPurgeTombstone From(string root, UninstallReceipt receipt)
    {
        receipt.Validate(root);
        if (receipt.Phase != UninstallPhase.VolumesRemoved || receipt.Plan.Mode != UninstallMode.Purge)
            throw new UsageException("Purge tombstone requires durable VolumesRemoved authority.");
        _ = receipt.Advance(UninstallPhase.Purged);
        return new(1, receipt.Plan.Operation, receipt.PlanHash, receipt.Plan.OperatorOwner, "purged");
    }

    /// <summary>Syntax alone is insufficient: independently verify the exact retained release and operator payload bytes.</summary>
    internal ReleaseBundle Validate(string root)
    {
        if (Schema != 1 || Operation == Guid.Empty || !ReleaseContract.Hash(PlanHash) || Result != "purged")
            throw new UsageException("Invalid purge tombstone.");
        OperatorOwner.Validate();
        var bundle = ReleaseStore.Select(root, OperatorOwner);
        ReleaseContract.RequireUse(bundle.Manifest, bundle.Manifest.Operator.Version);
        return bundle;
    }

    /// <summary>Strict bounded protected JSON rejects extra or duplicate fields before selecting any retained owner.</summary>
    internal static UninstallPurgeTombstone? Load(string root)
    {
        var path = Path.Combine(root, Name);
        if (!UninstallHistory.Exists(path)) return null;
        using var json = JsonDocument.Parse(UninstallPreparation.Read(root, path, 4096));
        foreach (var value in new[] { json.RootElement, json.RootElement.GetProperty("OperatorOwner") })
            if (value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
                throw new UsageException("Duplicate purge tombstone fields.");
        var tombstone = json.RootElement.Deserialize<UninstallPurgeTombstone>(ArchiveContract.Json)
            ?? throw new UsageException("Missing purge tombstone.");
        tombstone.Validate(root);
        return tombstone;
    }

    /// <summary>The current full receipt owns a matching final transition; contradictory pairs never grant terminal authority.</summary>
    internal void RequireMatch(string root, UninstallReceipt? receipt)
    {
        if (receipt is not null && this != From(root, receipt))
            throw new UsageException("Purge tombstone contradicts the current uninstall receipt.");
        UninstallPurge.RequireTerminalRoot(root, receipt is not null, true);
    }

    /// <summary>Fresh setup consumes only its originally observed terminal evidence under the caller's host mutation lock.</summary>
    internal static void Consume(string root, UninstallPurgeTombstone expected)
    {
        var current = Load(root) ?? throw new UsageException("Purge tombstone changed before fresh setup publication.");
        if (current != expected || UninstallReceipt.Load(root) is not null)
            throw new UsageException("Unresolved or changed purge authority prevents fresh setup.");
        current.RequireMatch(root, null);
        using var directory = new SafeDirectory(root);
        directory.Delete(Name);
    }
}
