using System.Text.Json;
using System.Text.RegularExpressions;

namespace WayfarerCtl;

/// <summary>Receipted forward preparation uses the target application, never a second schema migration launch.</summary>
internal sealed class UpdateHistoryPreparation(IProcessRunner runner, ITerminal? terminal = null)
{
    /// <summary>At most one preparation-only start per explicit resume; independent inspection owns completion.</summary>
    internal async Task<UpdateReceipt> EnsureAsync(string root, UpdateReceipt receipt, bool allowResume, CancellationToken token)
    {
        receipt = await ReconcileAsync(root, receipt, token);
        var preparation = new UpdatePreparation(runner);
        var evidence = await preparation.InspectHistoryAsync(root, receipt.Plan.Target, receipt.Plan.Operation, token);
        if (Ready(evidence)) return receipt with { HistoryReconciliation = evidence };
        ShowReadiness(evidence);
        if (!allowResume)
            throw new IOException("Schema maintenance completed but history preparation is incomplete. Keep writers stopped, correct history indexes/qualification/capacity, then use update --resume; schema migration will not be relaunched.");
        await new UpdateRuntime(runner).RequireExclusiveAsync(receipt, token);
        await preparation.CapacityAsync(root, receipt.Plan.Current, token);
        var owner = new RestoreContainers(runner);
        if (receipt.HistoryContainer is null)
        {
            receipt = receipt with { HistoryContainer = receipt.Plan.Current.Project + "-update-history-" + receipt.Plan.Operation.ToString("N") };
            receipt.Save(root);
        }
        if (receipt.HistoryContainerId is null)
        {
            var id = (await owner.Required(["create", "--name", receipt.HistoryContainer,
                "--label", "wayfarer.update=" + receipt.Plan.Operation.ToString("D"),
                "--label", "wayfarer.update-project=" + receipt.Plan.Current.Project,
                "--label", "wayfarer.history-update=" + receipt.Plan.Operation.ToString("D"),
                "--restart=no", "--pull=never", "--log-opt=max-size=1m", "--log-opt=max-file=1",
                .. UpdatePreparation.Maintenance(root, receipt.Plan.Target), "--entrypoint=dotnet",
                "ghcr.io/stef-k/wayfarer@" + receipt.Plan.Target.AppDigest, "Wayfarer.dll", "database", "prepare-history"], token)).Trim();
            receipt = receipt with { HistoryContainerId = id };
            receipt.Save(root);
        }
        // Recheck the actual helper even after successful create, before granting a start.
        receipt = await ReconcileAsync(root, receipt, token);
        receipt = receipt with { HistoryAttempts = checked(receipt.HistoryAttempts + 1) };
        receipt.Save(root);
        terminal?.Write("Resuming target history preparation; committed checkpoints retained. Maintenance may take longer than application startup.");
        await owner.Required(["start", receipt.HistoryContainerId!], token);
        await FollowAsync(runner, terminal, receipt.HistoryContainerId!, token);
        receipt = await ReconcileAsync(root, receipt, token);
        evidence = await preparation.InspectHistoryAsync(root, receipt.Plan.Target, receipt.Plan.Operation, token);
        if (!Ready(evidence))
        {
            ShowReadiness(evidence);
            throw new IOException("Target history preparation remains incomplete; retain the recovery set, correct prerequisites and explicitly resume again.");
        }
        return receipt with { HistoryReconciliation = evidence };
    }

    /// <summary>Lost create/start acknowledgements bind exact stopped helpers; missing bound identities never authorize replacement.</summary>
    internal async Task<UpdateReceipt> ReconcileAsync(string root, UpdateReceipt receipt, CancellationToken token)
    {
        if (receipt.HistoryContainer is null) return receipt;
        var owner = new RestoreContainers(runner);
        var inspection = await runner.RunAsync(["inspect", receipt.HistoryContainer], null, token);
        if (inspection.Code != 0)
        {
            var names = (await owner.Required(["ps", "-a", "--format", "{{.Names}}"], token)).Split('\n');
            var labelled = await owner.Required(["ps", "-aq", "--filter", "label=wayfarer.history-update=" + receipt.Plan.Operation.ToString("D")], token);
            if (receipt.HistoryContainerId is not null || names.Contains(receipt.HistoryContainer) || !string.IsNullOrWhiteSpace(labelled))
                throw new IOException("Preparation helper absence cannot be proven; retain and reconcile its exact identity.");
            return receipt;
        }
        using var document = JsonDocument.Parse(inspection.Output);
        var helper = document.RootElement[0];
        UpdateRuntime.VerifyMaintenanceHelper(root, receipt, helper, history: true);
        receipt = receipt with { HistoryContainerId = helper.GetProperty("Id").GetString() };
        receipt.Save(root);
        return receipt;
    }

    /// <summary>Application-owned status must affirm both distinct generations; no schema or helper acknowledgement substitutes.</summary>
    internal static bool Ready(string evidence)
    {
        using var document = JsonDocument.Parse(evidence);
        var root = document.RootElement;
        var generations = root.GetProperty("Generations").EnumerateArray().ToArray();
        if (generations.Length != 2 || !generations.Select(item => item.GetProperty("Kind").GetString()).Order()
                .SequenceEqual(new[] { "text", "time" })) throw new IOException("Invalid target history inspection.");
        return root.GetProperty("Ready").GetBoolean() && generations.All(item => item.GetProperty("Ready").GetBoolean());
    }

    /// <summary>Expose only typed target readiness diagnostics, without row identifiers or arbitrary failure payloads.</summary>
    private void ShowReadiness(string evidence)
    {
        using var document = JsonDocument.Parse(evidence);
        foreach (var status in document.RootElement.GetProperty("Generations").EnumerateArray())
            terminal?.Write($"History {status.GetProperty("Kind").GetString()}: pending={status.GetProperty("Pending").GetInt64()}; " +
                $"indexes-ready={status.GetProperty("IndexesReady").GetBoolean()}; qualified={status.GetProperty("Qualified").GetBoolean()}. " +
                "Correct missing indexes/extension privileges or use a qualified target runtime before resuming.");
    }

    /// <summary>Follow maintenance without the ten-minute process deadline; cancellation preserves durable helper intent.</summary>
    internal static async Task FollowAsync(IProcessRunner runner, ITerminal? terminal, string id, CancellationToken token)
    {
        var result = await runner.RunAsync(["logs", "--follow", "--tail", "20", id], null, token,
            line => WriteProgress(terminal, line));
        if (result.Code != 0) throw new IOException("Maintenance output interrupted; reconcile the retained helper before resume.");
    }

    /// <summary>Only fixed phase/count output crosses the private child-output boundary; arbitrary diagnostics remain private.</summary>
    internal static void WriteProgress(ITerminal? terminal, string line)
    {
        if (line == "History preparation: verifying current generations; keep writers stopped until maintenance completes." ||
            Regex.IsMatch(line, @"\AHistory preparation: (time|text); prepared=[0-9]+; pending=[0-9]+; ready=(True|False)\z"))
            terminal?.Write(line);
    }
}
