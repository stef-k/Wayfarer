using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Durable two-receipt ownership join; unresolved update intent is never removed to enter restore.</summary>
public static class UpdateRestoreHandoff
{
    /// <summary>Restore may target the retained old release only when both exact operation identities agree.</summary>
    public static void Require(string root, RestorePlan plan)
    {
        var update = UpdateReceipt.Load(root) ?? throw new IOException("Update handoff receipt missing.");
        if (plan.FromUpdate != update.Plan.Operation || update.RestoreOperation != plan.Operation ||
            !update.MigrationPossible || plan.Archive != update.RecoveryArchive || plan.ArchiveSha256 != update.RecoverySha256 ||
            plan.Target.Release != update.Plan.Current.Release || plan.OperatorOwner != update.Plan.OperatorOwner ||
            JsonSerializer.Serialize(plan.Target) != JsonSerializer.Serialize(update.Plan.Current))
            throw new IOException("Update/restore ownership join differs from retained authority.");
    }

    /// <summary>Restore acceptance resolves ownership without calling a failed forward update Accepted.</summary>
    public static void Complete(string root, RestoreReceipt restore)
    {
        if (restore.Plan.FromUpdate is null) return;
        Require(root, restore.Plan);
        if (restore.Phase != RestorePhase.Accepted) throw new IOException("Restore handoff has not accepted.");
        var update = UpdateReceipt.Load(root)!;
        (update with { RestoreAccepted = true }).Save(root);
    }
}
