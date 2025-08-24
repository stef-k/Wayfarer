namespace Wayfarer.Models.Dtos
{
    /// <summary>
    /// DTO for geographic bounding box
    /// Shared between backend API and mobile app
    /// </summary>
    public class BoundingBoxDto
    {
        /// <summary>
        /// Northern latitude boundary
        /// </summary>
        public double North { get; set; }

        /// <summary>
        /// Southern latitude boundary
        /// </summary>
        public double South { get; set; }

        /// <summary>
        /// Eastern longitude boundary
        /// </summary>
        public double East { get; set; }

        /// <summary>
        /// Western longitude boundary
        /// </summary>
        public double West { get; set; }

        /// <summary>
        /// Calculate the area covered by this bounding box (rough estimate)
        /// </summary>
        /// <returns>Area in square degrees</returns>
        public double GetAreaSquareDegrees()
        {
            return Math.Abs((North - South) * (East - West));
        }

        /// <summary>
        /// Check if a location is within this bounding box
        /// </summary>
        /// <param name="latitude">Latitude to check</param>
        /// <param name="longitude">Longitude to check</param>
        /// <returns>True if location is within bounds</returns>
        public bool Contains(double latitude, double longitude)
        {
            return latitude >= South && latitude <= North && 
                   longitude >= West && longitude <= East;
        }

        /// <summary>
        /// Returns a string representation of the bounding box
        /// </summary>
        public override string ToString()
        {
            return $"N:{North:F4}, S:{South:F4}, E:{East:F4}, W:{West:F4}";
        }
    }
}