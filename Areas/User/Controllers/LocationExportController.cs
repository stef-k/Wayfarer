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
    public async Task<IActionResult> GeoJson()
    {
        var locs = await UserLocations().ToListAsync();

        var features = new FeatureCollection();
        foreach (var loc in locs)
        {
            var attrs = new AttributesTable
            {
                { "Id", loc.Id },
                { "TimestampUtc", loc.Timestamp },
                { "LocalTimestamp", loc.LocalTimestamp },
                { "TimeZoneId", loc.TimeZoneId },
                { "Accuracy", loc.Accuracy },
                { "Altitude", loc.Altitude },
                { "Speed", loc.Speed },
                { "Activity", loc.ActivityType?.Name },
                { "Address", loc.FullAddress },
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
    public async Task<IActionResult> Csv()
    {
        var locs = await UserLocations().ToListAsync(); // Force client-side evaluation

        var flatLocs = locs.Select(l => new
        {
            l.Id,
            TimestampUtc = l.Timestamp.ToString("o"),
            LocalTimestamp = l.LocalTimestamp.ToString("o"),
            l.TimeZoneId,
            Longitude = l.Coordinates.X,
            Latitude = l.Coordinates.Y,
            l.Accuracy,
            l.Altitude,
            l.Speed,
            Activity = l.ActivityType?.Name,
            Address = l.FullAddress,
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
    public async Task<IActionResult> Gpx()
    {
        var locs = await UserLocations().ToListAsync();

        var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
        using (var xw = XmlWriter.Create(ms, settings))
        {
            xw.WriteStartDocument();
            xw.WriteStartElement("gpx", "http://www.topografix.com/GPX/1/1");
            xw.WriteAttributeString("version", "1.1");
            xw.WriteAttributeString("creator", "Wayfarer");

            xw.WriteStartElement("trk");
            xw.WriteElementString("name", "Wayfarer Location Export");
            xw.WriteStartElement("trkseg");

            foreach (var loc in locs)
            {
                xw.WriteStartElement("trkpt");
                xw.WriteAttributeString("lat", loc.Coordinates.Y.ToString(CultureInfo.InvariantCulture));
                xw.WriteAttributeString("lon", loc.Coordinates.X.ToString(CultureInfo.InvariantCulture));
                if (loc.Altitude.HasValue)
                    xw.WriteElementString("ele", loc.Altitude.Value.ToString(CultureInfo.InvariantCulture));
                xw.WriteElementString("time", loc.Timestamp.ToString("o"));
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
    public async Task<IActionResult> Kml()
    {
        var locs = await UserLocations().ToListAsync();

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
                xw.WriteStartElement("Placemark");
                xw.WriteElementString("name", loc.Timestamp.ToString("o"));
                if (!string.IsNullOrEmpty(loc.Notes))
                    xw.WriteElementString("description", loc.Notes);

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