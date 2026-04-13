using Microsoft.Extensions.Caching.Memory;

namespace Wayfarer.Services;

/// <summary>
/// In-process hot cache for zoom &gt;= 9 tile metadata plus a throttling marker for LastAccessed writes.
/// Durable tile metadata in Postgres remains authoritative; this cache is an optimization hint only.
/// </summary>
public sealed class TileMetadataHotCache : IDisposable
{
    /// <summary>
    /// Fixed pessimistic estimate used to convert the admin-facing MB budget into an approximate entry cap.
    /// Each metadata entry stores ExpiresAtUtc, ETag, and LastModifiedUpstream only.
    /// </summary>
    public const int EstimatedBytesPerHotMetadataEntry = 768;

    private static readonly TimeSpan LastAccessedThrottleInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<TileMetadataHotCache> _logger;
    private readonly object _syncLock = new();

    private IMemoryCache _metadataCache = new MemoryCache(new MemoryCacheOptions());
    private readonly IMemoryCache _touchMarkerCache = new MemoryCache(new MemoryCacheOptions());
    private int _configuredSizeMb = int.MinValue;
    private long _configuredEntryLimit = -1;
    private bool _disposed;

    public TileMetadataHotCache(ILogger<TileMetadataHotCache> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the hot metadata cache is enabled and contains an entry for the tile.
    /// Disabled mode bypasses the cache entirely.
    /// </summary>
    public bool TryGet(int hotCacheSizeMb, int zoom, int x, int y, out HotTileMetadataCacheEntry? metadata)
    {
        metadata = null;
        if (!TryGetMetadataCache(hotCacheSizeMb, out var metadataCache))
        {
            return false;
        }

        return metadataCache.TryGetValue(BuildMetadataKey(zoom, x, y), out metadata);
    }

    /// <summary>
    /// Inserts or updates a hot metadata entry using the current deterministic size limit.
    /// </summary>
    public void Set(int hotCacheSizeMb, int zoom, int x, int y, HotTileMetadataCacheEntry metadata)
    {
        if (!TryGetMetadataCache(hotCacheSizeMb, out var metadataCache))
        {
            return;
        }

        metadataCache.Set(
            BuildMetadataKey(zoom, x, y),
            metadata,
            new MemoryCacheEntryOptions
            {
                Size = 1
            });
    }

    /// <summary>
    /// Removes the hot metadata and LastAccessed throttle marker for a tile.
    /// </summary>
    public void Remove(int zoom, int x, int y)
    {
        _metadataCache.Remove(BuildMetadataKey(zoom, x, y));
        _touchMarkerCache.Remove(BuildTouchMarkerKey(zoom, x, y));
    }

    /// <summary>
    /// Clears all in-process metadata and throttle markers, used after purge/reset operations.
    /// </summary>
    public void Clear()
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();
            _metadataCache.Dispose();
            _metadataCache = new MemoryCache(new MemoryCacheOptions());
            _configuredSizeMb = int.MinValue;
            _configuredEntryLimit = -1;
        }

        CompactTouchMarkers();
    }

    /// <summary>
    /// Returns true once per tile per five-minute window so callers can throttle LastAccessed DB writes.
    /// </summary>
    public bool ShouldPersistLastAccessed(int zoom, int x, int y)
    {
        var key = BuildTouchMarkerKey(zoom, x, y);
        if (_touchMarkerCache.TryGetValue(key, out _))
        {
            return false;
        }

        _touchMarkerCache.Set(
            key,
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = LastAccessedThrottleInterval
            });

        return true;
    }

    /// <summary>
    /// Seeds or refreshes the LastAccessed throttle marker after a durable DB-backed metadata operation.
    /// </summary>
    public void MarkLastAccessedPersisted(int zoom, int x, int y)
    {
        _touchMarkerCache.Set(
            BuildTouchMarkerKey(zoom, x, y),
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = LastAccessedThrottleInterval
            });
    }

    /// <summary>
    /// Computes the approximate maximum hot metadata entries for the current admin setting.
    /// Returns null when the feature is disabled.
    /// </summary>
    public static long? GetApproximateEntryLimit(int hotCacheSizeMb)
    {
        if (hotCacheSizeMb == -1)
        {
            return null;
        }

        return (long)Math.Floor(hotCacheSizeMb * 1024d * 1024d / EstimatedBytesPerHotMetadataEntry);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _metadataCache.Dispose();
            _touchMarkerCache.Dispose();
            _disposed = true;
        }
    }

    private bool TryGetMetadataCache(int hotCacheSizeMb, out IMemoryCache metadataCache)
    {
        ThrowIfDisposed();

        if (hotCacheSizeMb == -1)
        {
            lock (_syncLock)
            {
                if (_configuredSizeMb != -1)
                {
                    _metadataCache.Dispose();
                    _metadataCache = new MemoryCache(new MemoryCacheOptions());
                    _configuredSizeMb = -1;
                    _configuredEntryLimit = -1;
                }
            }

            metadataCache = _metadataCache;
            return false;
        }

        var entryLimit = GetApproximateEntryLimit(hotCacheSizeMb) ?? 0;
        lock (_syncLock)
        {
            if (_configuredSizeMb != hotCacheSizeMb || _configuredEntryLimit != entryLimit)
            {
                _metadataCache.Dispose();
                _metadataCache = new MemoryCache(new MemoryCacheOptions
                {
                    SizeLimit = entryLimit
                });
                _configuredSizeMb = hotCacheSizeMb;
                _configuredEntryLimit = entryLimit;
                _logger.LogDebug(
                    "Configured tile metadata hot cache for {HotCacheSizeMb} MB (~{EntryLimit} entries).",
                    hotCacheSizeMb,
                    entryLimit);
            }

            metadataCache = _metadataCache;
        }

        return true;
    }

    private void CompactTouchMarkers()
    {
        try
        {
            if (_touchMarkerCache is MemoryCache touchMarkerCache)
            {
                touchMarkerCache.Compact(1.0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compact tile metadata touch markers.");
        }
    }

    private static string BuildMetadataKey(int zoom, int x, int y) => $"tile-meta:{zoom}:{x}:{y}";

    private static string BuildTouchMarkerKey(int zoom, int x, int y) => $"tile-touch:{zoom}:{x}:{y}";

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
/// Metadata-only hot cache entry for zoom &gt;= 9 tiles.
/// </summary>
public sealed class HotTileMetadataCacheEntry
{
    /// <summary>
    /// Expiry timestamp used to decide whether the cached file can be served directly.
    /// </summary>
    public required DateTime? ExpiresAtUtc { get; init; }

    /// <summary>
    /// ETag used for conditional revalidation after expiry.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Last-Modified value from the upstream provider used for conditional revalidation.
    /// </summary>
    public DateTime? LastModifiedUpstream { get; init; }
}
