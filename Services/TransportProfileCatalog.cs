using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services;

/// <summary>
/// Provides database-authoritative transport-profile lookup and dependency rules.
/// </summary>
public sealed class TransportProfileCatalog
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>Initializes the catalog over the application database.</summary>
    public TransportProfileCatalog(ApplicationDbContext dbContext) => _dbContext = dbContext;

    /// <summary>Returns active editor choices in deterministic catalog order.</summary>
    public async Task<IReadOnlyList<EditorTransportModeDto>> GetEditorOptionsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.TransportProfiles
            .AsNoTracking()
            .Where(profile => profile.IsActive)
            .OrderBy(profile => profile.SortOrder)
            .ThenBy(profile => profile.Label)
            .ThenBy(profile => profile.Key)
            .Select(profile => new EditorTransportModeDto(profile.Key, profile.Label, profile.PlanningSpeedKmh))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Resolves a requested key when it is active, or when an edit preserves its current inactive key.
    /// </summary>
    public async Task<string?> ResolveEditorModeAsync(string? requestedKey, string? currentKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestedKey))
        {
            return string.Empty;
        }

        var normalized = TransportProfile.NormalizeKey(requestedKey);
        var profile = await _dbContext.TransportProfiles.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Key == normalized, cancellationToken);
        if (profile == null)
        {
            return null;
        }

        return profile.IsActive || string.Equals(normalized, TransportProfile.NormalizeKey(currentKey ?? string.Empty), StringComparison.Ordinal)
            ? profile.Key
            : null;
    }

    /// <summary>Returns the planning speed for a known profile without treating null as automatic-capable.</summary>
    public Task<double?> GetPlanningSpeedAsync(string key, CancellationToken cancellationToken = default) =>
        _dbContext.TransportProfiles.AsNoTracking()
            .Where(profile => profile.Key == TransportProfile.NormalizeKey(key))
            .Select(profile => profile.PlanningSpeedKmh)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Returns dependencies and whether the pre-#405 boundary permits a speed mutation.</summary>
    public async Task<TransportProfileSpeedChangeGate> CanChangePlanningSpeedAsync(Guid profileId, double? proposedSpeed, CancellationToken cancellationToken = default)
    {
        var key = await _dbContext.TransportProfiles.AsNoTracking()
            .Where(profile => profile.Id == profileId)
            .Select(profile => profile.Key)
            .SingleAsync(cancellationToken);
        var referenced = await _dbContext.Segments.CountAsync(segment => segment.TransportProfileId == profileId, cancellationToken);
        return new TransportProfileSpeedChangeGate(referenced == 0, referenced, proposedSpeed);
    }
}

/// <summary>Describes the approved pre-#405 speed-change gate.</summary>
public sealed record TransportProfileSpeedChangeGate(bool Allowed, int ReferencedSegments, double? ProposedSpeed);
