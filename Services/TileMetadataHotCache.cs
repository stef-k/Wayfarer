using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Wayfarer.Services;

/// <summary>
/// In-process hot cache for zoom &gt;= 9 tile metadata plus a throttling marker for LastAccessed writes.
/// Durable tile metadata in Postgres remains authoritative; this cache is an optimization hint only.
/// </summary>
public sealed class TileMetadataHotCache : IDisposable
{
    private const string CompatibilityProviderIdentity = "legacy-test-provider";
    /// <summary>
    /// Fixed pessimistic estimate used to convert the admin-facing MB budget into an approximate entry cap.
    /// Each metadata entry stores ExpiresAtUtc, ETag, and LastModifiedUpstream only.
    /// </summary>
    public const int EstimatedBytesPerHotMetadataEntry = 768;

    private static readonly TimeSpan LastAccessedThrottleInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<TileMetadataHotCache> _logger;
    private readonly object _syncLock = new();
    private readonly ConcurrentDictionary<string, byte> _touchClaims = new();

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
    public bool TryGet(
        int hotCacheSizeMb,
        string providerIdentity,
        int zoom,
        int x,
        int y,
        out HotTileMetadataCacheEntry? metadata)
    {
        metadata = null;
        if (!TryGetMetadataCache(hotCacheSizeMb, out var metadataCache))
        {
            return false;
        }

        return metadataCache.TryGetValue(BuildMetadataKey(providerIdentity, zoom, x, y), out metadata);
    }

    /// <summary>Compatibility overload for existing coordinate-only unit fixtures.</summary>
    public bool TryGet(int hotCacheSizeMb, int zoom, int x, int y, out HotTileMetadataCacheEntry? metadata) =>
        TryGet(hotCacheSizeMb, CompatibilityProviderIdentity, zoom, x, y, out metadata);

    /// <summary>
    /// Inserts or updates a hot metadata entry using the current deterministic size limit.
    /// </summary>
    public void Set(
        int hotCacheSizeMb,
        string providerIdentity,
        int zoom,
        int x,
        int y,
        HotTileMetadataCacheEntry metadata)
    {
        if (!TryGetMetadataCache(hotCacheSizeMb, out var metadataCache))
        {
            return;
        }

        metadataCache.Set(
            BuildMetadataKey(providerIdentity, zoom, x, y),
            metadata,
            new MemoryCacheEntryOptions
            {
                Size = 1
            });
    }

    /// <summary>Compatibility overload for existing coordinate-only unit fixtures.</summary>
    public void Set(int hotCacheSizeMb, int zoom, int x, int y, HotTileMetadataCacheEntry metadata) =>
        Set(hotCacheSizeMb, CompatibilityProviderIdentity, zoom, x, y, metadata);

    /// <summary>
    /// Removes the hot metadata and LastAccessed throttle marker for a tile.
    /// </summary>
    public void Remove(string providerIdentity, int zoom, int x, int y)
    {
        var touchKey = BuildTouchMarkerKey(providerIdentity, zoom, x, y);
        _metadataCache.Remove(BuildMetadataKey(providerIdentity, zoom, x, y));
        _touchMarkerCache.Remove(touchKey);
        _touchClaims.TryRemove(touchKey, out _);
    }

    /// <summary>Compatibility overload for existing coordinate-only unit fixtures.</summary>
    public void Remove(int zoom, int x, int y) =>
        Remove(CompatibilityProviderIdentity, zoom, x, y);

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

        _touchClaims.Clear();
        CompactTouchMarkers();
    }

    /// <summary>
    /// Atomically claims responsibility for persisting LastAccessed for a tile.
    /// Returns true for at most one caller while there is no active five-minute cooldown.
    /// </summary>
    public bool TryBeginLastAccessedPersist(string providerIdentity, int zoom, int x, int y)
    {
        var key = BuildTouchMarkerKey(providerIdentity, zoom, x, y);
        if (_touchMarkerCache.TryGetValue(key, out _))
        {
            return false;
        }

        return _touchClaims.TryAdd(key, 0);
    }

    /// <summary>Compatibility overload for existing coordinate-only unit fixtures.</summary>
    public bool TryBeginLastAccessedPersist(int zoom, int x, int y) =>
        TryBeginLastAccessedPersist(CompatibilityProviderIdentity, zoom, x, y);

    /// <summary>
    /// Starts the five-minute LastAccessed cooldown after a durable DB-backed metadata operation succeeds.
    /// </summary>
    public void CompleteLastAccessedPersist(string providerIdentity, int zoom, int x, int y)
    {
        var key = BuildTouchMarkerKey(providerIdentity, zoom, x, y);
        _touchMarkerCache.Set(
            key,
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = LastAccessedThrottleInterval
            });
        _touchClaims.TryRemove(key, out _);
    }

    /// <summary>Compatibility overload for existing coordinate-only unit fixtures.</summary>
    public void CompleteLastAccessedPersist(int zoom, int x, int y) =>
        CompleteLastAccessedPersist(CompatibilityProviderIdentity, zoom, x, y);

    /// <summary>
    /// Releases an in-flight LastAccessed claim without starting the cooldown.
    /// Used when the DB update did not complete successfully so later requests can retry.
    /// </summary>
    public void AbortLastAccessedPersist(string providerIdentity, int zoom, int x, int y)
    {
        _touchClaims.TryRemove(BuildTouchMarkerKey(providerIdentity, zoom, x, y), out _);
    }

    /// <summary>Compatibility overload for existing coordinate-only unit fixtures.</summary>
    public void AbortLastAccessedPersist(int zoom, int x, int y) =>
        AbortLastAccessedPersist(CompatibilityProviderIdentity, zoom, x, y);

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

    private static string BuildMetadataKey(string providerIdentity, int zoom, int x, int y) =>
        $"tile-meta:{providerIdentity}:{zoom}:{x}:{y}";

    private static string BuildTouchMarkerKey(string providerIdentity, int zoom, int x, int y) =>
        $"tile-touch:{providerIdentity}:{zoom}:{x}:{y}";

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
