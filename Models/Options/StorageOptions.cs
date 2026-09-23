namespace Wayfarer.Models.Options;

/// <summary>Explicit runtime storage roots bound from the Storage configuration section.</summary>
public sealed class StorageOptions
{
    /// <summary>Durable application data root; null selects the Development default.</summary>
    public string? DataRoot { get; set; }

    /// <summary>Rebuildable cache root; null selects the Development default.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>Exact operational log root; null selects the Development default.</summary>
    public string? LogRoot { get; set; }

    /// <summary>Ephemeral working root; null selects the Development default.</summary>
    public string? TempRoot { get; set; }
}
