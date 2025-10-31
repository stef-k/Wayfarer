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
public class LocationExportController : Controller
{
    private readonly ApplicationDbContext _db;
    public LocationExportController(ApplicationDbContext db) => _db = db;

    // helper to get only this user’s locations, in time order
    private IQueryable<Models.Location> UserLocations()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return _db.Locations
            .Where(l => l.UserId == userId)
            .OrderBy(l => l.Timestamp);
    }

    [HttpGet]
    /// <summary>
    /// Exports the signed-in user's locations to GeoJSON, including reverse-geocoded details.
    /// </summary>
    public async Task<IActionResult> GeoJson()
    {
        var locs = await UserLocations().ToListAsync();

        var features = new FeatureCollection();
        foreach (var loc in locs)
        {
            var shortAddress = loc.Address ?? loc.FullAddress;
            var attrs = new AttributesTable
            {
                { "Id", loc.Id },
                { "TimestampUtc", DateTime.SpecifyKind(loc.Timestamp, DateTimeKind.Utc) },
                { "LocalTimestamp", loc.LocalTimestamp },
                { "TimeZoneId", loc.TimeZoneId },
                { "Accuracy", loc.Accuracy },
                { "Altitude", loc.Altitude },
                { "Speed", loc.Speed },
                { "Activity", loc.ActivityType?.Name },
                { "Address", shortAddress },
                { "FullAddress", loc.FullAddress },
                { "AddressNumber", loc.AddressNumber },
                { "StreetName", loc.StreetName },
                { "PostCode", loc.PostCode },
                { "Place", loc.Place },
                { "Region", loc.Region },
                { "Country", loc.Country },
                { "Notes", loc.Notes },
            };
            features.Add(new Feature(loc.Coordinates, attrs));
        }

        var ms = new MemoryStream();
        var serializer = GeoJsonSerializer.Create();
        using (var sw = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            serializer.Serialize(sw, features);
            sw.Flush();
        }

        ms.Position = 0;
        return File(ms, "application/geo+json", $"{GenerateTitle()}.geojson");
    }

    [HttpGet]
    /// <summary>
    /// Exports the signed-in user's locations to CSV with rich address metadata.
    /// </summary>
    public async Task<IActionResult> Csv()
    {
        var locs = await UserLocations().ToListAsync(); // Force client-side evaluation

        var flatLocs = locs.Select(l => new
        {
            l.Id,
            TimestampUtc = DateTime.SpecifyKind(l.Timestamp, DateTimeKind.Utc).ToString("o"),
            LocalTimestamp = l.LocalTimestamp.ToString("o"),
            l.TimeZoneId,
            Longitude = l.Coordinates.X,
            Latitude = l.Coordinates.Y,
            l.Accuracy,
            l.Altitude,
            l.Speed,
            Activity = l.ActivityType?.Name,
            Address = l.Address,
            FullAddress = l.FullAddress,
            AddressNumber = l.AddressNumber,
            StreetName = l.StreetName,
            PostCode = l.PostCode,
            Place = l.Place,
            Region = l.Region,
            Country = l.Country,
            Notes = l.Notes
        });

        var ms = new MemoryStream();
        await using (var sw = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true))
        using (var csv = new CsvWriter(sw, CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(flatLocs);
            await sw.FlushAsync();
        }

        ms.Position = 0;

        return File(ms, "text/csv", $"{GenerateTitle()}.csv");
    }

    [HttpGet]
    /// <summary>
    /// Exports the signed-in user's locations to GPX with Wayfarer extensions.
    /// </summary>
    public async Task<IActionResult> Gpx()
    {
        var locs = await UserLocations().ToListAsync();

        static void WriteGpxExtension(XmlWriter writer, string localName, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                writer.WriteElementString("wf", localName, "https://wayfarer.app/schemas/gpx", value);
            }
        }

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
                   || loc.Accuracy.HasValue
                   || loc.Speed.HasValue
                   || !string.IsNullOrWhiteSpace(loc.ActivityType?.Name)
                   || !string.IsNullOrWhiteSpace(loc.Notes);
        }

        var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
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
                    WriteGpxExtension(xw, "address", loc.Address);
                    WriteGpxExtension(xw, "fullAddress", loc.FullAddress);
                    WriteGpxExtension(xw, "addressNumber", loc.AddressNumber);
                    WriteGpxExtension(xw, "streetName", loc.StreetName);
                    WriteGpxExtension(xw, "postCode", loc.PostCode);
                    WriteGpxExtension(xw, "place", loc.Place);
                    WriteGpxExtension(xw, "region", loc.Region);
                    WriteGpxExtension(xw, "country", loc.Country);
                    WriteGpxExtension(xw, "notes", loc.Notes);
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

        static void WriteKmlData(XmlWriter writer, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            writer.WriteStartElement("Data", "http://www.opengis.net/kml/2.2");
            writer.WriteAttributeString("name", name);
            writer.WriteElementString("value", "http://www.opengis.net/kml/2.2", value);
            writer.WriteEndElement();
        }

        var ms = new MemoryStream(); // Do NOT use 'using' here
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };

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
                if (!string.IsNullOrEmpty(loc.Notes))
                    xw.WriteElementString("description", loc.Notes);

                xw.WriteStartElement("ExtendedData");
                WriteKmlData(xw, "TimestampUtc", timestampUtc.ToString("o"));
                WriteKmlData(xw, "LocalTimestamp", loc.LocalTimestamp.ToString("o"));
                WriteKmlData(xw, "TimeZoneId", loc.TimeZoneId);
                WriteKmlData(xw, "Accuracy", loc.Accuracy?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "Altitude", loc.Altitude?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "Speed", loc.Speed?.ToString(CultureInfo.InvariantCulture));
                WriteKmlData(xw, "Activity", loc.ActivityType?.Name);
                WriteKmlData(xw, "Address", loc.Address);
                WriteKmlData(xw, "FullAddress", loc.FullAddress);
                WriteKmlData(xw, "AddressNumber", loc.AddressNumber);
                WriteKmlData(xw, "StreetName", loc.StreetName);
                WriteKmlData(xw, "PostCode", loc.PostCode);
                WriteKmlData(xw, "Place", loc.Place);
                WriteKmlData(xw, "Region", loc.Region);
                WriteKmlData(xw, "Country", loc.Country);
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

    private string GenerateTitle()
    {
        return $"Wayfarer_Locations_Export_{DateTime.Now}";
    }
}
