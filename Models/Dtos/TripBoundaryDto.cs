namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// DTO for trip boundary API response
    /// Used by GET /api/trips/{id}/boundary endpoint
    /// </summary>
    public class TripBoundaryDto
    {
        /// <summary>
        /// Trip identifier
        /// </summary>
        public Guid TripId { get; set; }

        /// <summary>
        /// Trip name for display
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Geographic bounding box of the trip
        /// Mobile app uses this to calculate tile coordinates
        /// </summary>
        public BoundingBoxDto BoundingBox { get; set; } = new();
    }
}
