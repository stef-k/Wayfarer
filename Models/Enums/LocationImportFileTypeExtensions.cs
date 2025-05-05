using System;
using System.Collections.Generic;

namespace Wayfarer.Models.Enums
{
    public static class LocationImportFileTypeExtensions
    {
        private static readonly Dictionary<LocationImportFileType, string[]> _allowedExtensions = new()
        {
            [LocationImportFileType.GoogleTimeline] = new[] { ".json" },
            [LocationImportFileType.WayfarerGeoJson] = new[] { ".geojson", ".json" },
            [LocationImportFileType.Gpx] = new[] { ".gpx" },
            [LocationImportFileType.GeoJson] = new[] { ".geojson", ".json" },
            [LocationImportFileType.Kml] = new[] { ".kml" },
            [LocationImportFileType.Csv] = new[] { ".csv" }
        };

        public static IEnumerable<string> GetAllowedExtensions(this LocationImportFileType fileType)
        {
            return _allowedExtensions.TryGetValue(fileType, out var extensions)
                ? extensions
                : Array.Empty<string>();
        }

        public static bool IsExtensionValid(this LocationImportFileType fileType, string extension)
        {
            return GetAllowedExtensions(fileType)
                .Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
    }
}