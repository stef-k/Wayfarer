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
        public Point TileLocation { get; set; }

        // Track when the tile was last accessed (for eviction purposes)
        public DateTime LastAccessed { get; set; }

        // Track the size of the tile in bytes (useful for managing overall cache size)
        public int Size { get; set; }

        // This could be a reference to the actual file location or just an identifier
        // indicating that the tile is stored on disk
        public string TileFilePath { get; set; } 

        // To manage how long the tile should be kept, in this case, we do not need expiration logic
        // Expiration logic has been replaced with the eviction mechanism in the cache service
        
        // Concurrency token used to avoid race conditions
        /// <summary>
        /// Will be mapped to PostgreSQL's xmin system column.
        /// </summary>
        [Timestamp]
        public uint RowVersion { get; set; }
    }
}