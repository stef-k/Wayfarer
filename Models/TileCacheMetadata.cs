using NetTopologySuite.Geometries;
using System;
using System.ComponentModel.DataAnnotations;

namespace Wayfarer.Models
{
    public class TileCacheMetadata
    {
        public int Id { get; set; }
        
        public int Zoom { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        
        // The location of the tile (X and Y coordinates) stored as a PostGIS Point
        public required Point TileLocation { get; set; }

        // Track when the tile was last accessed (for eviction purposes)
        public DateTime LastAccessed { get; set; }

        // Track the size of the tile in bytes (useful for managing overall cache size)
        public int Size { get; set; }

        // This could be a reference to the actual file location or just an identifier
        // indicating that the tile is stored on disk
        public required string TileFilePath { get; set; }

        /// <summary>
        /// ETag value from the upstream tile server's response.
        /// Sent as If-None-Match on re-validation requests after expiry.
        /// </summary>
        [MaxLength(200)]
        public string? ETag { get; set; }

        /// <summary>
        /// Last-Modified header value from the upstream tile server's response.
        /// Sent as If-Modified-Since on re-validation requests after expiry.
        /// </summary>
        public DateTime? LastModifiedUpstream { get; set; }

        /// <summary>
        /// When the cached tile expires based on upstream Cache-Control/Expires headers.
        /// Before this time, the tile is served directly without re-validation.
        /// After this time, a conditional request is sent to check freshness.
        /// </summary>
        public DateTime? ExpiresAtUtc { get; set; }

        // Concurrency token used to avoid race conditions
        /// <summary>
        /// Will be mapped to PostgreSQL's xmin system column.
        /// </summary>
        [Timestamp]
        public uint RowVersion { get; set; }
    }
}