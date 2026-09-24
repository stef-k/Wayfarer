using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

public partial class ProxiedImageCacheService
{
    /// <inheritdoc />
    public async Task<ProxiedImageCacheStoreResult> SetAsync(string cacheKey, byte[] bytes, string contentType)
    {
        var settings = _settingsService.GetSettings();
        if (settings.MaxCacheImageSizeInMB < 0 || !ImageCacheStorage.IsCacheKey(cacheKey))
            return ProxiedImageCacheStoreResult.Failure;

        var filePath = _storage.CurrentPath(ImageCacheStorage.CreateReference(cacheKey), cacheKey);
        var tempFilePath = CreateTempImagePath(filePath);
        try
        {
            Directory.CreateDirectory(_storage.CurrentRoot);
            await File.WriteAllBytesAsync(tempFilePath, bytes);
        }
        catch (Exception ex)
        {
            TryDeleteTempImage(tempFilePath);
            _logger.LogError(ex, "Error writing proxy image file for key {CacheKey}.", cacheKey);
            return ProxiedImageCacheStoreResult.Failure;
        }

        await _cacheLock.WaitAsync();
        try
        {
            var existing = await _dbContext.ImageCacheMetadata.FirstOrDefaultAsync(m => m.CacheKey == cacheKey);
            if (existing != null)
            {
                return await ReplaceExistingEntryAsync(existing, tempFilePath, bytes, contentType);
            }

            var maxSizeBytes = settings.MaxCacheImageSizeInMB * 1024L * 1024L;
            while (Interlocked.Read(ref _currentCacheSize) + bytes.Length > maxSizeBytes)
            {
                var evictedCount = await EvictLruEntriesAsync();
                if (evictedCount == 0) break;
            }

            return await StoreNewEntryAsync(cacheKey, filePath, tempFilePath, bytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching proxy image for key {CacheKey}.", cacheKey);
            return ProxiedImageCacheStoreResult.Failure;
        }
        finally
        {
            TryDeleteTempImage(tempFilePath);
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Stores a new metadata row and removes unreferenced bytes if metadata fails.
    /// </summary>
    private async Task<ProxiedImageCacheStoreResult> StoreNewEntryAsync(
        string cacheKey,
        string filePath,
        string tempFilePath,
        byte[] bytes,
        string contentType)
    {
        var metadata = new ImageCacheMetadata
        {
            CacheKey = cacheKey,
            ContentType = contentType,
            FilePath = ImageCacheStorage.CreateReference(cacheKey),
            Size = bytes.Length,
            CreatedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow
        };

        _dbContext.ImageCacheMetadata.Add(metadata);
        var published = false;
        try
        {
            ReplaceImageFileAtomically(tempFilePath, filePath);
            published = true;
            await SaveMetadataChangesAsync();
            Interlocked.Add(ref _currentCacheSize, bytes.Length);
        }
        catch
        {
            _dbContext.ImageCacheMetadata.Remove(metadata);
            if (published) TryDeleteTempImage(filePath);
            return ProxiedImageCacheStoreResult.Failure;
        }

        _logger.LogInformation("Cached proxy image: key={CacheKey}, size={Size} bytes.", cacheKey, bytes.Length);
        return ProxiedImageCacheStoreResult.Success;
    }

    /// <summary>
    /// Stores replacement bytes without overwriting the old usable file before metadata succeeds.
    /// </summary>
    private async Task<ProxiedImageCacheStoreResult> ReplaceExistingEntryAsync(
        ImageCacheMetadata existing,
        string tempFilePath,
        byte[] bytes,
        string contentType)
    {
        var oldFilePath = existing.FilePath;
        var oldContentType = existing.ContentType;
        var oldSize = existing.Size;
        var oldCreatedAt = existing.CreatedAt;
        var oldLastAccessed = existing.LastAccessed;
        var oldPhysicalPath = _storage.Resolve(existing);
        var newReference = ImageCacheStorage.CreateReference(existing.CacheKey, Guid.NewGuid());
        var newFilePath = _storage.CurrentPath(newReference, existing.CacheKey);

        try
        {
            // The metadata row is the commit point. New bytes live in an unreferenced
            // current-root generation until the row points at it, so failed metadata leaves the
            // old file and metadata usable. After metadata succeeds, old-file cleanup is best effort.
            ReplaceImageFileAtomically(tempFilePath, newFilePath);
            var now = DateTime.UtcNow;
            existing.FilePath = newReference;
            existing.ContentType = contentType;
            existing.Size = bytes.Length;
            existing.CreatedAt = now;
            existing.LastAccessed = now;

            var saved = await SaveRefreshWithConcurrencyRetryAsync(existing);
            if (!saved)
            {
                RestoreMetadataValues(existing, oldFilePath, oldContentType, oldSize, oldCreatedAt, oldLastAccessed);
                TryDeleteTempImage(newFilePath);
                return ProxiedImageCacheStoreResult.Failure;
            }

            Interlocked.Add(ref _currentCacheSize, bytes.Length - oldSize);
            if (oldPhysicalPath != null) TryDeleteTempImage(oldPhysicalPath);
            _logger.LogInformation("Refreshed proxy image: key={CacheKey}, size={Size} bytes.",
                existing.CacheKey, bytes.Length);
            return ProxiedImageCacheStoreResult.Success;
        }
        catch (Exception ex)
        {
            RestoreMetadataValues(existing, oldFilePath, oldContentType, oldSize, oldCreatedAt, oldLastAccessed);
            TryDeleteTempImage(newFilePath);
            _logger.LogError(ex, "Error replacing proxy image file for key {CacheKey}.", existing.CacheKey);
            return ProxiedImageCacheStoreResult.Failure;
        }
    }

    /// <summary>
    /// Updates only access time. On xmin conflict, reload every database-current field before
    /// retrying so an old reader cannot restore a retired reference or stale content metadata.
    /// </summary>
    private async Task UpdateLastAccessedAsync(ImageCacheMetadata metadata)
    {
        var entry = _dbContext.Entry(metadata);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // Do not call Update: EF should mark only the changed LastAccessed property.
            metadata.LastAccessed = DateTime.UtcNow;
            try
            {
                await SaveMetadataChangesAsync();
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                await entry.ReloadAsync();
                if (entry.State == EntityState.Detached) return;
            }
        }
    }

    /// <summary>
    /// Persists the full refresh intent with retry while retaining the uncommitted generation.
    /// </summary>
    private async Task<bool> SaveRefreshWithConcurrencyRetryAsync(ImageCacheMetadata metadata)
    {
        var attempts = 0;
        var updated = false;

        while (!updated && attempts < 3)
        {
            attempts++;
            try
            {
                _dbContext.ImageCacheMetadata.Update(metadata);
                await SaveMetadataChangesAsync();
                updated = true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                var entry = ex.Entries.Single();
                var databaseValues = await entry.GetDatabaseValuesAsync();

                if (databaseValues == null)
                {
                    _logger.LogWarning("Image cache metadata was deleted by another process for key {CacheKey}.",
                        metadata.CacheKey);
                    return false;
                }

                metadata.LastAccessed = DateTime.UtcNow;
                entry.OriginalValues.SetValues(databaseValues);
            }
        }

        return updated;
    }

    /// <summary>
    /// Creates a same-directory temporary path for atomic image replacement.
    /// </summary>
    private static string CreateTempImagePath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? ".";
        var fileName = Path.GetFileName(filePath);
        return Path.Combine(directory, $"{fileName}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Restores tracked metadata values after an uncommitted replacement failure.
    /// </summary>
    private static void RestoreMetadataValues(
        ImageCacheMetadata metadata,
        string filePath,
        string contentType,
        int size,
        DateTime createdAt,
        DateTime lastAccessed)
    {
        metadata.FilePath = filePath;
        metadata.ContentType = contentType;
        metadata.Size = size;
        metadata.CreatedAt = createdAt;
        metadata.LastAccessed = lastAccessed;
    }

    /// <summary>
    /// Replaces the final image using the active production or test hook.
    /// </summary>
    private static void ReplaceImageFileAtomically(string tempFilePath, string filePath) =>
        _replaceImageFile(tempFilePath, filePath);

    /// <summary>
    /// Replaces the final image with a same-directory temp file so readers never see partial bytes.
    /// </summary>
    private static void ReplaceImageFileAtomicallyCore(string tempFilePath, string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Replace(tempFilePath, filePath, null);
            return;
        }

        File.Move(tempFilePath, filePath);
    }

    /// <summary>
    /// Saves metadata changes using the active production or test hook.
    /// </summary>
    private Task<int> SaveMetadataChangesAsync() => _saveMetadataChanges(_dbContext);

    /// <summary>
    /// Overrides image replacement for deterministic tests.
    /// </summary>
    internal static void SetImageFileReplacerForTesting(Action<string, string>? replacer)
    {
        _replaceImageFile = replacer ?? ReplaceImageFileAtomicallyCore;
    }

    /// <summary>
    /// Overrides metadata persistence for deterministic tests.
    /// </summary>
    internal static void SetMetadataSaverForTesting(Func<ApplicationDbContext, Task<int>>? saver)
    {
        _saveMetadataChanges = saver ?? (dbContext => dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// Installs a narrow test-only hook before cache file reads.
    /// </summary>
    internal static void SetBeforeFileReadForTesting(Func<string, string, Task>? hook)
    {
        _beforeFileReadForTesting = hook ?? ((_, _) => Task.CompletedTask);
    }

    /// <summary>
    /// Deletes an unused temp file without masking the original write or replacement error.
    /// </summary>
    private static void TryDeleteTempImage(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
