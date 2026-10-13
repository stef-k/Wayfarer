namespace Wayfarer.Models;

/// <summary>Retains a removed duplicate's positive ID indefinitely for existing queued clients.</summary>
public sealed class ActivityTypeIdRedirect
{
    /// <summary>Removed ID; never allocated to a live Activity again.</summary>
    public int LegacyId { get; set; }

    /// <summary>Direct live survivor; there are no redirect chains.</summary>
    public int ActivityTypeId { get; set; }
}
