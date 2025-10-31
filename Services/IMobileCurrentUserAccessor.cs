using System.Threading;
using System.Threading.Tasks;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>
/// Provides access to the current mobile user resolved via API token.
/// </summary>
public interface IMobileCurrentUserAccessor
{
    Task<ApplicationUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    void Reset();
}
