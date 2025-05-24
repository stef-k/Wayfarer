using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Services
{
    public class LocationService
    {
        private readonly ApplicationDbContext _dbContext;

        public LocationService(ApplicationDbContext dbContext)
            => _dbContext = dbContext;

        public async Task<(List<PublicLocationDto> Locations, int TotalItems)> GetLocationsAsync(
            double minLongitude, double minLatitude,
            double maxLongitude, double maxLatitude,
            double zoomLevel, string userId, CancellationToken cancellationToken)
        {
            double eps = zoomLevel <= 5 ? 0.1 : (zoomLevel <= 10 ? 0.05 : 0.01);
            double expand = zoomLevel <= 5 ? eps : (zoomLevel <= 10 ? eps * 0.5 : eps * 0.25);

            minLongitude -= expand;
            maxLongitude += expand;
            minLatitude  -= expand;
            maxLatitude  += expand;

            var countCmd = _dbContext.Database.GetDbConnection().CreateCommand();
            countCmd.CommandText = @"
                SELECT COUNT(*)
                FROM ""public"".""Locations""
                WHERE ST_X((""Coordinates""::geometry)) BETWEEN @minLon AND @maxLon
                  AND ST_Y((""Coordinates""::geometry)) BETWEEN @minLat AND @maxLat
                  AND ""UserId"" = @userId
            ";
            countCmd.Parameters.Add(new NpgsqlParameter("minLon", minLongitude));
            countCmd.Parameters.Add(new NpgsqlParameter("maxLon", maxLongitude));
            countCmd.Parameters.Add(new NpgsqlParameter("minLat", minLatitude));
            countCmd.Parameters.Add(new NpgsqlParameter("maxLat", maxLatitude));
            countCmd.Parameters.Add(new NpgsqlParameter("userId", userId));

            if (countCmd.Connection.State != System.Data.ConnectionState.Open)
                await countCmd.Connection.OpenAsync(cancellationToken);

            var rawCount = await countCmd.ExecuteScalarAsync(cancellationToken);
            int totalItems = Convert.ToInt32(rawCount);

            var settings = await _dbContext.ApplicationSettings.FirstOrDefaultAsync(cancellationToken);
            int locationTimeThreshold = settings?.LocationTimeThresholdMinutes ?? 10;

            const int ExhaustiveFetchThreshold = 400;
            if (totalItems <= ExhaustiveFetchThreshold)
            {
                var sqlAll = @"
                    SELECT *
                    FROM ""public"".""Locations""
                    WHERE ST_X((""Coordinates""::geometry)) BETWEEN @minLon AND @maxLon
                      AND ST_Y((""Coordinates""::geometry)) BETWEEN @minLat AND @maxLat
                      AND ""UserId"" = @userId
                    ORDER BY ""LocalTimestamp"" DESC
                ";
                var sqlParams = new[]
                {
                    new NpgsqlParameter("minLon", minLongitude),
                    new NpgsqlParameter("maxLon", maxLongitude),
                    new NpgsqlParameter("minLat", minLatitude),
                    new NpgsqlParameter("maxLat", maxLatitude),
                    new NpgsqlParameter("userId", userId)
                };

                var allLocations = await _dbContext.Locations
                    .FromSqlRaw(sqlAll, sqlParams)
                    .Include(l => l.ActivityType)
                    .ToListAsync(cancellationToken);

                var dtos = MapToDto(allLocations, locationTimeThreshold);
                return (dtos, totalItems);
            }

            int precision = zoomLevel <= 5 ? 2 : (zoomLevel <= 10 ? 4 : 6);
            int limit     = zoomLevel <= 5 ? 500 : (zoomLevel <= 10 ? 1000 : 3000);

            var locations = await GetSampledLocationsAsync(
                minLongitude, minLatitude, maxLongitude, maxLatitude,
                precision, limit, userId, cancellationToken);

            var grid = DivideMapIntoQuadrants(minLongitude, minLatitude, maxLongitude, maxLatitude, zoomLevel);
            locations = SampleLocationsByQuadrants(locations, grid, zoomLevel).ToList();

            var resultDtos = MapToDto(locations, locationTimeThreshold);
            return (resultDtos, totalItems);
        }

        private async Task<List<Location>> GetSampledLocationsAsync(
            double minLon, double minLat, double maxLon, double maxLat,
            int precision, int limit, string userId, CancellationToken ct)
        {
            var sql = @"
                WITH ranked AS (
                    SELECT ""Id"",
                           ROW_NUMBER() OVER (
                               PARTITION BY ST_GeoHash((""Coordinates""::geometry), @p_precision)
                               ORDER BY ""LocalTimestamp"" DESC
                           ) AS rn
                    FROM ""public"".""Locations""
                    WHERE ST_X((""Coordinates""::geometry)) BETWEEN @p_minLon AND @p_maxLon
                      AND ST_Y((""Coordinates""::geometry)) BETWEEN @p_minLat AND @p_maxLat
                      AND ""UserId"" = @p_userId
                )
                SELECT l.*
                FROM ranked r
                JOIN ""public"".""Locations"" l ON l.""Id"" = r.""Id""
                WHERE r.rn = 1
                LIMIT @p_limit
            ";

            var parameters = new[]
            {
                new NpgsqlParameter("p_precision", precision),
                new NpgsqlParameter("p_minLon", minLon),
                new NpgsqlParameter("p_maxLon", maxLon),
                new NpgsqlParameter("p_minLat", minLat),
                new NpgsqlParameter("p_maxLat", maxLat),
                new NpgsqlParameter("p_userId", userId),
                new NpgsqlParameter("p_limit", limit)
            };

            return await _dbContext.Locations
                .FromSqlRaw(sql, parameters)
                .Include(l => l.ActivityType)
                .ToListAsync(ct);
        }

        private List<(double minLon, double minLat, double maxLon, double maxLat)> DivideMapIntoQuadrants(
            double minLon, double minLat, double maxLon, double maxLat, double zoomLevel)
        {
            int cellsPerSide = zoomLevel switch
            {
                <= 3 => 2,
                <= 5 => 4,
                <= 8 => 6,
                <= 10 => 8,
                <= 12 => 10,
                <= 15 => 12,
                _    => 16
            };

            double lonStep = (maxLon - minLon) / cellsPerSide;
            double latStep = (maxLat - minLat) / cellsPerSide;

            var cells = new List<(double, double, double, double)>();
            for (int i = 0; i < cellsPerSide; i++)
            {
                for (int j = 0; j < cellsPerSide; j++)
                {
                    double cellMinLon = minLon + i * lonStep;
                    double cellMaxLon = cellMinLon + lonStep;
                    double cellMinLat = minLat + j * latStep;
                    double cellMaxLat = cellMinLat + latStep;

                    cells.Add((cellMinLon, cellMinLat, cellMaxLon, cellMaxLat));
                }
            }

            return cells;
        }

        private IEnumerable<Location> SampleLocationsByQuadrants(
            List<Location> locations,
            List<(double minLon, double minLat, double maxLon, double maxLat)> grid,
            double zoomLevel)
        {
            int perCell = zoomLevel switch
            {
                <= 5 => 1,
                <= 10 => 2,
                <= 14 => 3,
                _ => 5
            };

            foreach (var cell in grid)
            {
                var matching = locations.Where(l =>
                    l.Coordinates.X >= cell.minLon && l.Coordinates.X <= cell.maxLon &&
                    l.Coordinates.Y >= cell.minLat && l.Coordinates.Y <= cell.maxLat)
                    .OrderByDescending(l => l.LocalTimestamp)
                    .Take(perCell);

                foreach (var item in matching)
                    yield return item;
            }
        }

        private List<PublicLocationDto> MapToDto(List<Location> locations, int timeThreshold)
        {
            return locations.Select(l => new PublicLocationDto
            {
                Id = l.Id,
                Coordinates = l.Coordinates,
                Timestamp = l.Timestamp,
                LocalTimestamp = CoordinateTimeZoneConverter.ConvertUtcToLocal(
                    l.Coordinates.Y, l.Coordinates.X,
                    DateTime.SpecifyKind(l.Timestamp, DateTimeKind.Utc)),
                Timezone = l.TimeZoneId,
                Accuracy = l.Accuracy,
                Altitude = l.Altitude,
                Speed = l.Speed,
                LocationType = l.LocationType,
                ActivityType = l.ActivityType?.Name ?? "Unknown",
                Address = l.Address,
                FullAddress = l.FullAddress,
                StreetName = l.StreetName,
                PostCode = l.PostCode,
                Place = l.Place,
                Region = l.Region,
                Country = l.Country,
                Notes = l.Notes,
                VehicleId = l.VehicleId,
                IsLatestLocation = false,
                LocationTimeThresholdMinutes = timeThreshold
            }).ToList();
        }
    }
}
