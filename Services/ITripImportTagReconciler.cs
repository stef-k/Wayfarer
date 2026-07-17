using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Server-authoritative reconciliation of Wayfarer KML tag transport values.</summary>
public interface ITripImportTagReconciler
{
    /// <summary>Returns tracked global tags in first-occurrence canonical-slug order.</summary>
    Task<IReadOnlyList<Tag>> ReconcileAsync(IEnumerable<string> tokens, CancellationToken cancellationToken = default);
}
