using Microsoft.AspNetCore.Mvc.Rendering;

namespace Wayfarer.Models.ViewModels;

/// <summary>Catalog-only choices and aggregate preview; contains no owner or individual Location data.</summary>
public sealed class ActivityConsolidationViewModel
{
    /// <summary>Live source whose numeric ID will be permanently retired.</summary>
    public required ActivityConsolidationEntry Source { get; init; }

    /// <summary>Existing destination, populated only after an explicit selection.</summary>
    public ActivityConsolidationEntry? Destination { get; init; }

    /// <summary>Other live catalog names available as destinations.</summary>
    public required IReadOnlyList<SelectListItem> Destinations { get; init; }
}

/// <summary>Two-entry database snapshot; xmin is used only by manual consolidation confirmation.</summary>
public sealed record ActivityConsolidationEntry(int Id, string Name, string? Description, string Version, long LocationCount);
