using Microsoft.EntityFrameworkCore;

namespace Wayfarer.Models;

public partial class ApplicationDbContext
{
    /// <summary>
    /// Saves the isolated Region lifecycle footprint. The locked, freshly loaded Trip is stamped by
    /// property so stale metadata cannot be replayed by the general-purpose timestamp override.
    /// </summary>
    internal async Task SaveRegionDeletionAsync(Trip currentTrip, CancellationToken token)
    {
        Entry(currentTrip).Property(t => t.UpdatedAt).CurrentValue = DateTime.UtcNow;
        Entry(currentTrip).Property(t => t.UpdatedAt).IsModified = true;
        await base.SaveChangesAsync(token);
    }
}
