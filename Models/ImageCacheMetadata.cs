using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models
{
    /// <summary>
    /// Tracks cached proxy images on disk for LRU eviction and size management.
    /// Follows the same pattern as <see cref="TileCacheMetadata"/>.
    /// </summary>
    public class ImageCacheMetadata
    {
        public int Id { get; set; }

        /// <summary>
        /// SHA-256 hex hash of the proxy request parameters (url, maxWidth, maxHeight, quality, optimize).
        /// Used as the unique cache key and disk file name.
        /// </summary>
        [Required]
        [MaxLength(64)]
        public required string CacheKey { get; set; }

        /// <summary>
        /// MIME type of the cached image (e.g. "image/jpeg" or "image/png").
        /// Stored so the correct Content-Type can be set on cache hits without inspecting the file.
        /// </summary>
        [Required]
        [MaxLength(50)]
        public required string ContentType { get; set; }

        /// <summary>
        /// Absolute path to the cached image file on disk.
        /// </summary>
        [Required]
        public required string FilePath { get; set; }

        /// <summary>
        /// Size of the cached image file in bytes. Used for cache size tracking and LRU eviction.
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// When the image was first cached. Used for time-based expiry (ImageCacheExpiryDays).
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When the image was last served from cache. Used for LRU eviction ordering.
        /// </summary>
        public DateTime LastAccessed { get; set; }

        /// <summary>
        /// Concurrency token mapped to PostgreSQL's xmin system column.
        /// Prevents race conditions during concurrent updates.
        /// </summary>
        [Timestamp]
        public uint RowVersion { get; set; }
    }
}
