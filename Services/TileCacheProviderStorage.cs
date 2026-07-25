using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Util;

public partial class TileCacheService
{
    /// <summary>
    /// Retires at most fifty unscoped entries during normal maintenance when the active provider
    /// cannot prove canonical OSM provenance. Canonical OSM adopts entries lazily on lookup.
    /// </summary>
    internal async Task<int> RetireLegacyCacheBatchAsync(CancellationToken cancellationToken)
    {
        const int batchSize = 50;
        if (GetActiveProviderIdentity().CanAdoptLegacyOsm)
        {
            return 0;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            // Adoption uses the same lock, so every selected row is still proven unscoped.
            var legacyRows = await _dbContext.TileCacheMetadata
                .Where(tile => tile.ProviderIdentity == null)
                .OrderBy(tile => tile.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            var paths = legacyRows
                .Select(tile => tile.TileFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var protectedPaths = (await BuildScopedPathProtectionQuery(
                    _dbContext.TileCacheMetadata,
                    paths)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retiredSize = legacyRows.Sum(tile => (long)tile.Size);
            if (legacyRows.Count > 0)
            {
                _dbContext.TileCacheMetadata.RemoveRange(legacyRows);
                await _dbContext.SaveChangesAsync(cancellationToken);
                Interlocked.Add(ref _currentCacheSize, -retiredSize);
            }

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (protectedPaths.Contains(path))
                {
                    continue;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                var sidecarPath = GetSidecarPath(path);
                if (File.Exists(sidecarPath))
                {
                    File.Delete(sidecarPath);
                }
            }

            return legacyRows.Count;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>Restricts provider ownership protection to the bounded cleanup candidate paths.</summary>
    internal static IQueryable<string> BuildScopedPathProtectionQuery(
        IQueryable<TileCacheMetadata> metadata,
        string[] candidatePaths)
    {
        var normalizedCandidates = candidatePaths
            .Select(path => path.ToUpperInvariant())
            .ToArray();
        return metadata
            .Where(tile =>
                tile.ProviderIdentity != null &&
                normalizedCandidates.Contains(tile.TileFilePath.ToUpper()))
            .Select(tile => tile.TileFilePath);
    }

    /// <summary>Resolves the active non-secret cache identity from authoritative settings.</summary>
    private TileProviderCacheIdentity GetActiveProviderIdentity()
    {
        var settings = _applicationSettings.GetSettings();
        var preset = TileProviderCatalog.FindPreset(settings.TileProviderKey);
        var template = preset?.UrlTemplate ?? settings.TileProviderUrlTemplate;
        return TileProviderCatalog.CreateCacheIdentity(settings.TileProviderKey, template);
    }

    /// <summary>Builds a provider-scoped path without exposing provider configuration or credentials.</summary>
    private string GetProviderTilePath(
        string providerIdentity,
        string zoom,
        string x,
        string y) =>
        Path.Combine(_cacheDirectory, providerIdentity, $"{zoom}_{x}_{y}.png");

    /// <summary>Returns the active provider path for deterministic cache integration tests.</summary>
    internal string GetTileFilePathForTesting(string zoom, string x, string y)
    {
        var provider = GetActiveProviderIdentity();
        var scopedPath = GetProviderTilePath(provider.Fingerprint, zoom, x, y);
        var legacyPath = Path.Combine(_cacheDirectory, $"{zoom}_{x}_{y}.png");
        return File.Exists(scopedPath) ? scopedPath : legacyPath;
    }

    /// <summary>
    /// Executes shared cold work in a fresh scope so the initiating request may cancel independently.
    /// </summary>
    private async Task<TileRetrievalResult> RetrieveColdTileInFreshScopeAsync(
        string tileUrl,
        string zoom,
        string x,
        string y,
        string providerIdentity,
        string tileFilePath,
        TileProviderPolicy providerPolicy,
        string? clientIp,
        string? publicOrigin,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var service = scope?.ServiceProvider.GetService<TileCacheService>();
        if (service != null)
        {
            return await service.RetrieveColdTileCoreAsync(
                tileUrl, zoom, x, y, providerIdentity, tileFilePath, providerPolicy, clientIp, publicOrigin,
                cancellationToken);
        }

        // Isolated unit constructions may not provide a scope factory; production DI always does.
        return await RetrieveColdTileCoreAsync(
            tileUrl, zoom, x, y, providerIdentity, tileFilePath, providerPolicy, clientIp, publicOrigin,
            cancellationToken);
    }

    /// <summary>Downloads, persists, and maps one scheduler-owned cold series.</summary>
    private async Task<TileRetrievalResult> RetrieveColdTileCoreAsync(
        string tileUrl,
        string zoom,
        string x,
        string y,
        string providerIdentity,
        string tileFilePath,
        TileProviderPolicy providerPolicy,
        string? clientIp,
        string? publicOrigin,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(tileFilePath))
            {
                return TileRetrievalResult.Success(
                    await File.ReadAllBytesAsync(tileFilePath, cancellationToken));
            }
        }
        catch (IOException)
        {
            // The provider-scoped cache state changed concurrently; continue with owned fetch.
        }

        var fillResult = await CacheTileWithRetryAsync(
            tileUrl, zoom, x, y, providerIdentity, tileFilePath, providerPolicy,
            clientIp, allowHttpContext: false, publicOrigin, cancellationToken);
        try
        {
            if (File.Exists(tileFilePath))
            {
                return TileRetrievalResult.Success(
                    await File.ReadAllBytesAsync(tileFilePath, cancellationToken));
            }
        }
        catch (IOException)
        {
            // A concurrent bounded cleanup removed the file; return the owned fetch outcome.
        }

        return fillResult.Status switch
        {
            TileCacheFillStatus.NotFound => TileRetrievalResult.NotFound(),
            TileCacheFillStatus.PermanentFailure => TileRetrievalResult.PermanentFailure(),
            TileCacheFillStatus.BudgetRejected => TileRetrievalResult.Throttled(
                TilesController.BudgetRetryAfterSeconds),
            _ => TileRetrievalResult.TransientFailure(
                TileProviderRetryPolicy.GetBoundedRetryAfterSeconds(fillResult.RetryAfter))
        };
    }

    /// <summary>Seeds a default seven-day expiry on adopted metadata without one.</summary>
    private async Task SeedLegacyTileExpiryAsync(TileCacheMetadata meta)
    {
        meta.ExpiresAtUtc = DateTime.UtcNow.Add(DefaultCacheExpiry);
        try
        {
            await _dbContext.SaveChangesAsync();
            TrySetHotMetadataEntry(meta.ProviderIdentity!, meta.Zoom, meta.X, meta.Y, meta);
            _logger.LogDebug(
                "Seeded 7-day expiry for legacy tile z={Zoom} x={X} y={Y}",
                meta.Zoom, meta.X, meta.Y);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Legacy expiry seed skipped due to concurrency (non-critical)");
        }
    }

    /// <summary>Loads provider-scoped metadata and transactionally adopts proven legacy OSM rows.</summary>
    private async Task<TileCacheMetadata?> LoadAndTouchMetadataAsync(
        string providerIdentity,
        bool canAdoptLegacyOsm,
        int zoom,
        int x,
        int y)
    {
        var meta = await _dbContext.TileCacheMetadata
            .FirstOrDefaultAsync(t => t.ProviderIdentity == providerIdentity &&
                                      t.Zoom == zoom && t.X == x && t.Y == y);

        if (meta == null && canAdoptLegacyOsm)
        {
            await _cacheLock.WaitAsync();
            try
            {
                meta = await _dbContext.TileCacheMetadata
                    .FirstOrDefaultAsync(t => t.ProviderIdentity == null &&
                                              t.Zoom == zoom && t.X == x && t.Y == y);
                if (meta != null)
                {
                    meta.ProviderIdentity = providerIdentity;
                    try
                    {
                        await _dbContext.SaveChangesAsync();
                    }
                    catch (DbUpdateException)
                    {
                        _dbContext.Entry(meta).State = EntityState.Detached;
                        meta = await _dbContext.TileCacheMetadata
                            .FirstOrDefaultAsync(t => t.ProviderIdentity == providerIdentity &&
                                                      t.Zoom == zoom && t.X == x && t.Y == y);
                    }
                }
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        if (meta == null)
        {
            return null;
        }

        if (meta.LastAccessed < DateTime.UtcNow - LastAccessedThrottleInterval)
        {
            meta.LastAccessed = DateTime.UtcNow;
            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogDebug("LastAccessed update skipped due to concurrency (non-critical)");
            }
        }

        return meta;
    }

    /// <summary>Updates provider-scoped expiry metadata after a 304 response.</summary>
    private async Task UpdateTileExpiryScopedAsync(
        ApplicationDbContext dbContext,
        string providerIdentity,
        int zoom,
        int x,
        int y,
        string? etag,
        DateTime? lastModified,
        DateTime newExpiry)
    {
        var meta = await dbContext.TileCacheMetadata
            .FirstOrDefaultAsync(t => t.ProviderIdentity == providerIdentity &&
                                      t.Zoom == zoom && t.X == x && t.Y == y);
        if (meta == null)
        {
            return;
        }

        meta.ETag = etag;
        meta.LastModifiedUpstream = lastModified;
        meta.ExpiresAtUtc = newExpiry;
        meta.LastAccessed = DateTime.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync();
            TrySetHotMetadataEntry(providerIdentity, zoom, x, y, meta);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Expiry update skipped due to concurrency (non-critical)");
        }
    }

    /// <summary>Updates provider-scoped metadata after a successful 200 revalidation.</summary>
    private async Task UpdateTileAfterRevalidationScopedAsync(
        ApplicationDbContext dbContext,
        string providerIdentity,
        int zoom,
        int x,
        int y,
        int newSize,
        string? etag,
        DateTime? lastModified,
        DateTime newExpiry)
    {
        var meta = await dbContext.TileCacheMetadata
            .FirstOrDefaultAsync(t => t.ProviderIdentity == providerIdentity &&
                                      t.Zoom == zoom && t.X == x && t.Y == y);
        if (meta == null)
        {
            return;
        }

        var oldSize = meta.Size;
        meta.Size = newSize;
        meta.ETag = etag;
        meta.LastModifiedUpstream = lastModified;
        meta.ExpiresAtUtc = newExpiry;
        meta.LastAccessed = DateTime.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync();
            Interlocked.Add(ref _currentCacheSize, newSize - oldSize);
            TrySetHotMetadataEntry(providerIdentity, zoom, x, y, meta);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogDebug("Re-validation metadata update skipped due to concurrency (non-critical)");
        }
    }
}
