using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Receipt-owned normal removal reconciles the accepted inventory, fences consumers and never deletes a volume.</summary>
internal sealed class UninstallRuntime(IProcessRunner runner)
{
    /// <summary>Preserved retries allow independently owned canonical runtime, while retained storage still binds its original identity.</summary>
    internal async Task<UninstallResource[]> ValidateAsync(string root, UninstallReceipt receipt, UninstallHistory history,
        bool reactivating, CancellationToken token)
    {
        var plan = receipt.Plan;
        var actual = await new UninstallInventory(runner).DiscoverAsync(root, plan.Current, plan.Mode, history, token);
        if (actual.Any(resource => !plan.Resources.Any(planned => planned.Kind == resource.Kind && planned.Name == resource.Name)))
            throw new UsageException("Unplanned Docker resource prevents uninstall reconciliation.");
        foreach (var planned in plan.Resources.Where(resource => resource.Kind is
            UninstallResourceKind.Container or UninstallResourceKind.Network or UninstallResourceKind.Volume))
        {
            var current = Find(actual, planned);
            if (planned.Kind == UninstallResourceKind.Volume)
            {
                // Absent historical generations must stay absent. Newly created canonical cache/proxy storage has ordinary ownership.
                if (!(reactivating && planned.DockerId is null && planned.LifecycleOperation is null))
                    UninstallInventory.Reconcile(planned, current, planned.DockerId is null);
            }
            else if (reactivating)
            {
                if (current.DockerId is not null && planned.LifecycleOperation is not null)
                    throw new UsageException("Unexpected retained helper prevents reactivation.");
            }
            else
            {
                UninstallInventory.Reconcile(planned, current, true);
                var removed = planned.Kind == UninstallResourceKind.Container ? receipt.RemovedContainers : receipt.RemovedNetworks;
                if (current.DockerId is not null && (receipt.Phase >= UninstallPhase.RuntimeRemoved || removed.Contains(current.DockerId)))
                    throw new UsageException("Previously removed runtime resource reappeared.");
            }
        }
        return actual;
    }

    /// <summary>Advance only proven semantic cutoffs; per-resource absence is durable before the next deletion.</summary>
    internal async Task<UninstallReceipt> RemoveAsync(string root, UninstallReceipt receipt, UninstallHistory history, CancellationToken token)
    {
        if (receipt.Plan.Mode != UninstallMode.Normal) throw new UsageException("Purge execution is not enabled in this handoff.");
        if (receipt.Phase is UninstallPhase.Authorized or UninstallPhase.Fenced)
        {
            await FenceAsync(root, receipt, history, token);
            if (receipt.Phase == UninstallPhase.Authorized)
            {
                receipt = receipt.Advance(UninstallPhase.Fenced);
                receipt.Save(root);
            }
            receipt = await RemoveKindAsync(root, receipt, history, UninstallResourceKind.Container, token);
            receipt = await RemoveKindAsync(root, receipt, history, UninstallResourceKind.Network, token);
            await ValidateAsync(root, receipt, history, false, token);
            receipt = receipt.Advance(UninstallPhase.RuntimeRemoved);
            receipt.Save(root);
        }
        if (receipt.Phase == UninstallPhase.RuntimeRemoved)
        {
            await ValidateAsync(root, receipt, history, false, token);
            receipt = receipt.Advance(UninstallPhase.Preserved);
            receipt.Save(root);
        }
        return receipt;
    }

    /// <summary>Disable restart before graceful stop, reconcile each acknowledgement, and prove exclusive retained storage before Fenced.</summary>
    private async Task FenceAsync(string root, UninstallReceipt receipt, UninstallHistory history, CancellationToken token)
    {
        var containers = receipt.Plan.Resources.Where(resource => resource.Kind == UninstallResourceKind.Container &&
            resource.Action == UninstallAction.Remove && resource.DockerId is not null)
            .OrderBy(resource => resource.Role switch { "backup-scheduler" => 0, "wayfarer" => 1, "caddy" => 2, "db" => 3, _ => 4 });
        foreach (var container in containers)
        {
            var actual = await ValidateAsync(root, receipt, history, false, token);
            if (Find(actual, container).DockerId is null) continue;
            var seconds = container.Role == "backup-scheduler" ? "30" : "70";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(int.Parse(seconds) + 15));
            await InspectContainerAsync(container, deadline.Token);
            await AttemptAsync(["update", "--restart=no", container.DockerId!], deadline.Token);
            var facts = await InspectContainerAsync(container, deadline.Token);
            RequireStoppedPolicy(facts, false);
            await AttemptAsync(["stop", "--time", seconds, container.DockerId!], deadline.Token);
            facts = await InspectContainerAsync(container, deadline.Token);
            // Created containers have never run; Docker wait would otherwise wait indefinitely for a first exit.
            if (facts.GetProperty("State").GetProperty("Status").GetString() != "created")
                await AttemptAsync(["wait", container.DockerId!], deadline.Token);
            RequireStoppedPolicy(await InspectContainerAsync(container, deadline.Token), true);
        }
        var final = await ValidateAsync(root, receipt, history, false, token);
        foreach (var container in receipt.Plan.Resources.Where(resource => resource.Kind == UninstallResourceKind.Container && resource.DockerId is not null))
            if (Find(final, container).DockerId is not null)
                RequireStoppedPolicy(await InspectContainerAsync(container, token), true);
        foreach (var volume in final.Where(resource => resource.Kind == UninstallResourceKind.Volume && resource.DockerId is not null))
        {
            var consumers = await RequiredAsync(["ps", "-aq", "--no-trunc", "--filter", "volume=" + volume.Name], token);
            foreach (var id in consumers.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var planned = receipt.Plan.Resources.SingleOrDefault(resource => resource.Kind == UninstallResourceKind.Container && resource.DockerId == id)
                    ?? throw new UsageException("Unplanned retained-volume consumer prevents fencing.");
                RequireStoppedPolicy(await InspectContainerAsync(planned, token), true);
            }
        }
    }

    /// <summary>Only accepted present container/network IDs can be removed; a failed client still requires independently proven absence.</summary>
    private async Task<UninstallReceipt> RemoveKindAsync(string root, UninstallReceipt receipt, UninstallHistory history,
        UninstallResourceKind kind, CancellationToken token)
    {
        foreach (var planned in receipt.Plan.Resources.Where(resource => resource.Kind == kind &&
            resource.Action == UninstallAction.Remove && resource.DockerId is not null))
        {
            var actual = await ValidateAsync(root, receipt, history, false, token);
            if (Find(actual, planned).DockerId is not null)
            {
                if (kind == UninstallResourceKind.Container)
                {
                    RequireStoppedPolicy(await InspectContainerAsync(planned, token), true);
                    await AttemptAsync(["rm", planned.DockerId!], token);
                }
                else await AttemptAsync(["network", "rm", planned.DockerId!], token);
                actual = await ValidateAsync(root, receipt, history, false, token);
                if (Find(actual, planned).DockerId is not null) throw new IOException("Exact uninstall resource removal remains unresolved.");
            }
            var removed = kind == UninstallResourceKind.Container ? receipt.RemovedContainers : receipt.RemovedNetworks;
            if (removed.Contains(planned.DockerId!)) continue;
            receipt = kind == UninstallResourceKind.Container
                ? receipt with { RemovedContainers = [.. removed, planned.DockerId!] }
                : receipt with { RemovedNetworks = [.. removed, planned.DockerId!] };
            receipt.Save(root);
        }
        return receipt;
    }

    /// <summary>Full discovery proves absence; a missing exact helper entry is represented without inventing a resource.</summary>
    private static UninstallResource Find(UninstallResource[] actual, UninstallResource planned) =>
        actual.SingleOrDefault(resource => resource.Kind == planned.Kind && resource.Name == planned.Name)
        ?? planned with { DockerId = null, Evidence = null };

    /// <summary>Reinspect exact stable evidence immediately before mutation or fence proof, including the accepted name and ID.</summary>
    private async Task<JsonElement> InspectContainerAsync(UninstallResource planned, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await RequiredAsync(["container", "inspect", planned.DockerId!], token));
        if (document.RootElement.GetArrayLength() != 1) throw new UsageException("Ambiguous exact container inspection.");
        var facts = document.RootElement[0].Clone();
        UninstallInventory.Reconcile(planned, planned with { Name = facts.GetProperty("Name").GetString()!.TrimStart('/'),
            DockerId = facts.GetProperty("Id").GetString(), Evidence = UninstallInventory.Evidence(UninstallResourceKind.Container, facts) }, false);
        return facts;
    }

    /// <summary>Restart-disabled and stopped are daemon observations, never conclusions drawn from Docker client success.</summary>
    private static void RequireStoppedPolicy(JsonElement facts, bool stopped)
    {
        if (facts.GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString() != "no" ||
            stopped && facts.GetProperty("State").GetProperty("Running").GetBoolean())
            throw new UsageException("Uninstall container fence is not proven.");
    }

    /// <summary>Lost mutation acknowledgement is reconciled by the caller; cancellation still leaves forward receipt authority.</summary>
    private async Task AttemptAsync(string[] arguments, CancellationToken token)
    {
        try { await runner.RunAsync(arguments, null, token); }
        catch (IOException) { /* Only subsequent exact inspection can prove the intended result. */ }
    }

    /// <summary>Uncertain or oversized inspection output is never proof of absence or ownership.</summary>
    private async Task<string> RequiredAsync(string[] arguments, CancellationToken token)
    {
        var result = await runner.RunAsync(arguments, null, token);
        if (result.Code != 0 || result.Output.Length >= 262144) throw new IOException("Cannot inspect uninstall runtime authority.");
        return result.Output;
    }
}
