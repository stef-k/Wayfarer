using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using CsvHelper; // CsvWriter
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Features; // FeatureCollection, AttributesTable
using NetTopologySuite.Geometries;
using NetTopologySuite.IO; // GeoJsonSerializer
using Wayfarer.Models;

namespace Wayfarer.Areas.User.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public partial class LocationExportController : Controller
{
    private readonly ApplicationDbContext _db;
    public LocationExportController(ApplicationDbContext db) => _db = db;

    /// <summary>Loads the signed-in owner's history and current catalog names in timestamp order.</summary>
    private IQueryable<Models.Location> UserLocations()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return _db.Locations
            .Include(l => l.ActivityType)
            .Where(l => l.UserId == userId)
            .OrderBy(l => l.Timestamp);
    }

    [HttpGet]
    /// <summary>
    /// Exports the signed-in user's locations to GPX with Wayfarer extensions.
    /// </summary>
    public async Task<IActionResult> Gpx()
    {
        var locs = await UserLocations().ToListAsync();

        /// <summary>Writes optional extension text, retaining nonempty whitespace for LocationType.</summary>
        static void WriteGpxExtension(XmlWriter writer, string localName, string? value, bool preserveWhitespace = false)
        {
            if (!string.IsNullOrEmpty(value) && (preserveWhitespace || !string.IsNullOrWhiteSpace(value)))
            {
                writer.WriteElementString("wf", localName, "https://wayfarer.app/schemas/gpx", value);
            }
        }

        /// <summary>Includes nonempty LocationType even when it is the track point's only extension data.</summary>
        static bool HasGpxExtensionData(Wayfarer.Models.Location loc)
        {
            return !string.IsNullOrWhiteSpace(loc.TimeZoneId)
                   || !string.IsNullOrWhiteSpace(loc.Address)
                   || !string.IsNullOrWhiteSpace(loc.FullAddress)
                   || !string.IsNullOrWhiteSpace(loc.AddressNumber)
                   || !string.IsNullOrWhiteSpace(loc.StreetName)
                   || !string.IsNullOrWhiteSpace(loc.PostCode)
                   || !string.IsNullOrWhiteSpace(loc.Place)
                   || !string.IsNullOrWhiteSpace(loc.Region)
                   || !string.IsNullOrWhiteSpace(loc.Country)
                   || !string.IsNullOrWhiteSpace(loc.ProviderAddressLine1)
                   || !string.IsNullOrWhiteSpace(loc.ResolvedFeatureName)
                   || !string.IsNullOrWhiteSpace(loc.ResolvedFeatureType)
                   || !string.IsNullOrWhiteSpace(loc.ReverseGeocodingProvider)
                   || !string.IsNullOrWhiteSpace(loc.ReverseGeocodingStorageMode)
                   || loc.ReverseGeocodedAt.HasValue
                   || loc.Accuracy.HasValue
                   || loc.Speed.HasValue
                   || !string.IsNullOrWhiteSpace(loc.ActivityType?.Name)
                   || !string.IsNullOrEmpty(loc.LocationType)
                   || !string.IsNullOrWhiteSpace(Wayfarer.Util.RichNotes.Normalize(loc.Notes))
                   // Metadata fields
                   || loc.CoordinateOrigin != null || loc.ObservedAtUtc.HasValue || loc.ObservedAgeAtEventMs.HasValue
                   || !string.IsNullOrWhiteSpace(loc.Source)
                   || loc.IsUserInvoked.HasValue
                   || !string.IsNullOrWhiteSpace(loc.Provider)
                   || loc.Bearing.HasValue
                   || !string.IsNullOrWhiteSpace(loc.AppVersion)
                   || !string.IsNullOrWhiteSpace(loc.AppBuild)
                   || !string.IsNullOrWhiteSpace(loc.DeviceModel)
                   || !string.IsNullOrWhiteSpace(loc.OsVersion)
                   || loc.BatteryLevel.HasValue
                   || loc.IsCharging.HasValue;
        }

        var ms = new MemoryStream();
        // XML readers normalize literal CR; character references preserve significant Activity/type text.
        var settings = new XmlWriterSettings
        {
            Indent = true, Encoding = Encoding.UTF8, NewLineHandling = NewLineHandling.Entitize
        };
        using (var xw = XmlWriter.Create(ms, settings))
        {
            xw.WriteStartDocument();
            xw.WriteStartElement("gpx", "http://www.topografix.com/GPX/1/1");
            xw.WriteAttributeString("version", "1.1");
            xw.WriteAttributeString("creator", "Wayfarer");
            xw.WriteAttributeString("xmlns", "wf", null, "https://wayfarer.app/schemas/gpx");

            xw.WriteStartElement("trk");
            xw.WriteElementString("name", "Wayfarer Location Export");
            xw.WriteStartElement("trkseg");

            foreach (var loc in locs)
            {
                var timestampUtc = DateTime.SpecifyKind(loc.Timestamp, DateTimeKind.Utc);
                xw.WriteStartElement("trkpt");
                xw.WriteAttributeString("lat", loc.Coordinates.Y.ToString(CultureInfo.InvariantCulture));
                xw.WriteAttributeString("lon", loc.Coordinates.X.ToString(CultureInfo.InvariantCulture));
                if (loc.Altitude.HasValue)
                    xw.WriteElementString("ele", loc.Altitude.Value.ToString(CultureInfo.InvariantCulture));
                xw.WriteElementString("time", timestampUtc.ToString("o"));

                if (HasGpxExtensionData(loc))
                {
                    xw.WriteStartElement("extensions");
                    WriteGpxExtension(xw, "localTimestamp", loc.LocalTimestamp.ToString("o"));
                    WriteGpxExtension(xw, "timeZoneId", loc.TimeZoneId);
                    WriteGpxExtension(xw, "accuracy", loc.Accuracy?.ToString(CultureInfo.InvariantCulture));
                    WriteGpxExtension(xw, "speed", loc.Speed?.ToString(CultureInfo.InvariantCulture));
                    WriteGpxExtension(xw, "activity", loc.ActivityType?.Name);
                    var (locationType, encoded) = GetXmlLocationType(loc.LocationType);
                    WriteGpxExtension(xw, encoded ? "locationTypeUtf8Base64V1" : "locationType",
                        locationType, preserveWhitespace: true);
                    WriteGpxExtension(xw, "address", loc.Address);
                    WriteGpxExtension(xw, "fullAddress", loc.FullAddress);
                    WriteGpxExtension(xw, "addressNumber", loc.AddressNumber);
                    WriteGpxExtension(xw, "streetName", loc.StreetName);
                    WriteGpxExtension(xw, "postCode", loc.PostCode);
                    WriteGpxExtension(xw, "place", loc.Place);
                    WriteGpxExtension(xw, "region", loc.Region);
                    WriteGpxExtension(xw, "country", loc.Country);
                    WriteGpxExtension(xw, "providerAddressLine1", loc.ProviderAddressLine1);
                    WriteGpxExtension(xw, "resolvedFeatureName", loc.ResolvedFeatureName);
                    WriteGpxExtension(xw, "resolvedFeatureType", loc.ResolvedFeatureType);
                    WriteGpxExtension(xw, "reverseGeocodingProvider", loc.ReverseGeocodingProvider);
                    WriteGpxExtension(xw, "reverseGeocodingStorageMode", loc.ReverseGeocodingStorageMode);
                    WriteGpxExtension(xw, "reverseGeocodedAt", loc.ReverseGeocodedAt?.ToUniversalTime().ToString("O"));
                    WriteGpxExtension(xw, "notes", Wayfarer.Util.RichNotes.Normalize(loc.Notes));
                    // Metadata fields
                    WriteGpxExtension(xw, "coordinateOrigin", loc.CoordinateOrigin);
                    WriteGpxExtension(xw, "observedAtUtc", loc.ObservedAtUtc?.ToUniversalTime().ToString("O"));
                    WriteGpxExtension(xw, "observedAgeAtEventMs", loc.ObservedAgeAtEventMs?.ToString(CultureInfo.InvariantCulture));
                    WriteGpxExtension(xw, "source", loc.Source);
                    WriteGpxExtension(xw, "isUserInvoked", loc.IsUserInvoked?.ToString().ToLowerInvariant());
                    WriteGpxExtension(xw, "provider", loc.Provider);
                    WriteGpxExtension(xw, "bearing", loc.Bearing?.ToString(CultureInfo.InvariantCulture));
                    WriteGpxExtension(xw, "appVersion", loc.AppVersion);
                    WriteGpxExtension(xw, "appBuild", loc.AppBuild);
                    WriteGpxExtension(xw, "deviceModel", loc.DeviceModel);
                    WriteGpxExtension(xw, "osVersion", loc.OsVersion);
                    WriteGpxExtension(xw, "batteryLevel", loc.BatteryLevel?.ToString(CultureInfo.InvariantCulture));
                    WriteGpxExtension(xw, "isCharging", loc.IsCharging?.ToString().ToLowerInvariant());
                    xw.WriteEndElement(); // extensions
                }

                xw.WriteEndElement(); // trkpt
            }

            xw.WriteEndElement(); // trkseg
            xw.WriteEndElement(); // trk
            xw.WriteEndElement(); // gpx
            xw.WriteEndDocument();
            xw.Flush();
        }

        ms.Position = 0;
        return File(ms, "application/gpx+xml", $"{GenerateTitle()}.gpx");
    }

    [HttpGet]
    /// <summary>
    /// Exports the signed-in user's locations to KML with extended metadata.
    /// </summary>
    public async Task<IActionResult> Kml()
    {
        var locs = await UserLocations().ToListAsync();

        /// <summary>Writes optional data text, retaining nonempty whitespace for LocationType.</summary>
        static void WriteKmlData(XmlWriter writer, string name, string? value, bool preserveWhitespace = false)
        {
            if (string.IsNullOrEmpty(value) || (!preserveWhitespace && string.IsNullOrWhiteSpace(value)))
            {
                return;
            }

            writer.WriteStartElement("Data", "http://www.opengis.net/kml/2.2");
            writer.WriteAttributeString("name", name);
            writer.WriteElementString("value", "http://www.opengis.net/kml/2.2", value);
            writer.WriteEndElement();
        }

        var ms = new MemoryStream(); // Do NOT use 'using' here
        // Match GPX's lossless XML handling of significant carriage returns.
        var settings = new XmlWriterSettings
        {
            Indent = true, Encoding = Encoding.UTF8, NewLineHandling = NewLineHandling.Entitize
        };

        using (var xw = XmlWriter.Create(ms, settings))
        {
            xw.WriteStartDocument();
            xw.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
            xw.WriteStartElement("Document");
            xw.WriteElementString("name", "Wayfarer Location Export");

            foreach (var loc in locs)
            {
                var timestampUtc = DateTime.SpecifyKind(loc.Timestamp, DateTimeKind.Utc);
                xw.WriteStartElement("Placemark");
                xw.WriteElementString("name", timestampUtc.ToString("o"));
                if (!string.IsNullOrEmpty(Wayfarer.Util.RichNotes.Normalize(loc.Notes)))
                    xw.WriteElementString("description", Wayfarer.Util.RichNotes.Normalize(loc.Notes));

                xw.WriteStartElement("ExtendedData");
                WriteKmlData(xw, "TimestampUtc", timestampUtc.ToString("o"));
                WriteKmlData(xw, "LocalTimestamp", loc.LocalTimestamp.ToString("o"));
                WriteKmlData(xw, "TimeZoneId", loc.TimeZoneId);
                WriteKmlData(xw, "Accuracy", loc.Accuracy?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "Altitude", loc.Altitude?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "Speed", loc.Speed?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "Activity", loc.ActivityType?.Name);
                var (locationType, encoded) = GetXmlLocationType(loc.LocationType);
                WriteKmlData(xw, encoded ? "WayfarerLocationTypeUtf8Base64V1" : "LocationType",
                    locationType, preserveWhitespace: true);
                WriteKmlData(xw, "Address", loc.Address);
                WriteKmlData(xw, "FullAddress", loc.FullAddress);
                WriteKmlData(xw, "AddressNumber", loc.AddressNumber);
                WriteKmlData(xw, "StreetName", loc.StreetName);
                WriteKmlData(xw, "PostCode", loc.PostCode);
                WriteKmlData(xw, "Place", loc.Place);
                WriteKmlData(xw, "Region", loc.Region);
                WriteKmlData(xw, "Country", loc.Country);
                WriteKmlData(xw, "ProviderAddressLine1", loc.ProviderAddressLine1);
                WriteKmlData(xw, "ResolvedFeatureName", loc.ResolvedFeatureName);
                WriteKmlData(xw, "ResolvedFeatureType", loc.ResolvedFeatureType);
                WriteKmlData(xw, "ReverseGeocodingProvider", loc.ReverseGeocodingProvider);
                WriteKmlData(xw, "ReverseGeocodingStorageMode", loc.ReverseGeocodingStorageMode);
                WriteKmlData(xw, "ReverseGeocodedAt", loc.ReverseGeocodedAt?.ToUniversalTime().ToString("O"));
                // Metadata fields
                WriteKmlData(xw, "CoordinateOrigin", loc.CoordinateOrigin);
                WriteKmlData(xw, "ObservedAtUtc", loc.ObservedAtUtc?.ToUniversalTime().ToString("O"));
                WriteKmlData(xw, "ObservedAgeAtEventMs", loc.ObservedAgeAtEventMs?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "Source", loc.Source);
                WriteKmlData(xw, "IsUserInvoked", loc.IsUserInvoked?.ToString());
                WriteKmlData(xw, "Provider", loc.Provider);
                WriteKmlData(xw, "Bearing", loc.Bearing?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "AppVersion", loc.AppVersion);
                WriteKmlData(xw, "AppBuild", loc.AppBuild);
                WriteKmlData(xw, "DeviceModel", loc.DeviceModel);
                WriteKmlData(xw, "OsVersion", loc.OsVersion);
                WriteKmlData(xw, "BatteryLevel", loc.BatteryLevel?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "IsCharging", loc.IsCharging?.ToString());
                xw.WriteEndElement(); // ExtendedData

                xw.WriteStartElement("Point");
                var coords = $"{loc.Coordinates.X.ToString(CultureInfo.InvariantCulture)}," +
                             $"{loc.Coordinates.Y.ToString(CultureInfo.InvariantCulture)}," +
                             $"{loc.Altitude?.ToString(CultureInfo.InvariantCulture) ?? "0"}";
                xw.WriteElementString("coordinates", coords);
                xw.WriteEndElement(); // Point
                xw.WriteEndElement(); // Placemark
            }

            xw.WriteEndElement(); // Document
            xw.WriteEndElement(); // kml
            xw.WriteEndDocument();
            xw.Flush();
        }

        ms.Position = 0; // Rewind the stream
        return File(ms, "application/vnd.google-earth.kml+xml", $"{GenerateTitle()}.kml");
    }

    /// <summary>Uses the versioned fallback only for XML 1.0-incompatible types; invalid Unicode fails strict UTF-8 encoding.</summary>
    private static (string? Value, bool Encoded) GetXmlLocationType(string? value)
    {
        if (string.IsNullOrEmpty(value)) return (value, false);
        try
        {
            XmlConvert.VerifyXmlChars(value);
            return (value, false);
        }
        catch (XmlException)
        {
            return (Convert.ToBase64String(new UTF8Encoding(false, true).GetBytes(value)), true);
        }
    }

    private string GenerateTitle()
    {
        return $"Wayfarer_Locations_Export_{DateTime.Now}";
    }
}
