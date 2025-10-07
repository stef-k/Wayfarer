using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Parsers
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
            // 1) Expand bbox
            double eps = zoomLevel <= 5 ? 0.1
                : zoomLevel <= 10 ? 0.05
                : 0.01;
            double expand = zoomLevel <= 5 ? eps
                : zoomLevel <= 10 ? eps * 0.5
                : eps * 0.25;

            minLongitude -= expand;
            maxLongitude += expand;
            minLatitude -= expand;
            maxLatitude += expand;

            // 2) RAW-SQL COUNT
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

            int totalItems = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));

            // 2.5) Load settings
            var settings = await _dbContext.ApplicationSettings.FirstOrDefaultAsync(cancellationToken);
            int locationTimeThreshold = settings?.LocationTimeThresholdMinutes ?? 10;

            // 3) EARLY-EXIT if small
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

                var dtos = allLocations.Select(l => new PublicLocationDto
                {
                    Id = l.Id,
                    Coordinates = l.Coordinates,
                    Timestamp = l.Timestamp,
                    LocalTimestamp = CoordinateTimeZoneConverter
                        .ConvertUtcToLocal(
                            l.Coordinates.Y,
                            l.Coordinates.X,
                            DateTime.SpecifyKind(l.LocalTimestamp, DateTimeKind.Utc)),
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
                    LocationTimeThresholdMinutes = locationTimeThreshold
                }).ToList();

                return (dtos, totalItems);
            }

            // 4) ZOOM ≤5: per-country sampling with zoom-specific counts
            List<Location> locations;
            if (zoomLevel <= 5)
            {
                int perCountry = zoomLevel <= 3 ? 1 : zoomLevel == 4 ? 2 : 1;

                var countrySql = $@"
                    WITH ranked AS (
                      SELECT *,
                             ROW_NUMBER() OVER (
                               PARTITION BY ""Country""
                               ORDER BY ""LocalTimestamp"" DESC
                             ) AS rn
                        FROM ""public"".""Locations""
                       WHERE ST_X((""Coordinates""::geometry)) BETWEEN @minLon AND @maxLon
                         AND ST_Y((""Coordinates""::geometry)) BETWEEN @minLat AND @maxLat
                         AND ""UserId"" = @userId
                         AND ""Country"" IS NOT NULL
                    )
                    SELECT * FROM ranked WHERE rn <= {perCountry}
                ";
                var countryParams = new[]
                {
                    new NpgsqlParameter("minLon", minLongitude),
                    new NpgsqlParameter("maxLon", maxLongitude),
                    new NpgsqlParameter("minLat", minLatitude),
                    new NpgsqlParameter("maxLat", maxLatitude),
                    new NpgsqlParameter("userId", userId)
                };

                var countryBatch = await _dbContext.Locations
                    .FromSqlRaw(countrySql, countryParams)
                    .Include(l => l.ActivityType)
                    .ToListAsync(cancellationToken);

                var pickedIds = countryBatch.Select(l => l.Id).ToHashSet();

                if (zoomLevel == 5)
                {
                    int fillLimit = Math.Max(0, 500 - countryBatch.Count);
                    var fill = await GetSampledLocationsAsync(
                        minLongitude, minLatitude,
                        maxLongitude, maxLatitude,
                        precision: 2,
                        limit: fillLimit,
                        userId, cancellationToken);

                    locations = countryBatch.Concat(fill.Where(l => !pickedIds.Contains(l.Id))).ToList();
                }
                else
                {
                    locations = countryBatch;
                }
            }
            else if (zoomLevel <= 10)
            {
                int precision = 4;
                int limit = 1000;
                locations = await GetSampledLocationsAsync(
                    minLongitude, minLatitude,
                    maxLongitude, maxLatitude,
                    precision, limit,
                    userId, cancellationToken);
            }
            else
            {
                var sql = @"
                    SELECT *
                      FROM ""public"".""Locations""
                     WHERE ST_X((""Coordinates""::geometry)) BETWEEN @minLon AND @maxLon
                       AND ST_Y((""Coordinates""::geometry)) BETWEEN @minLat AND @maxLat
                       AND ""UserId"" = @userId
                     ORDER BY ""LocalTimestamp"" DESC
                     LIMIT 5000
                ";
                var parameters = new[]
                {
                    new NpgsqlParameter("minLon", minLongitude),
                    new NpgsqlParameter("maxLon", maxLongitude),
                    new NpgsqlParameter("minLat", minLatitude),
                    new NpgsqlParameter("maxLat", maxLatitude),
                    new NpgsqlParameter("userId", userId)
                };
                locations = await _dbContext.Locations
                    .FromSqlRaw(sql, parameters)
                    .Include(l => l.ActivityType)
                    .ToListAsync(cancellationToken);
            }

            var geocodingLevel = zoomLevel <= 5 ? "Country"
                : zoomLevel <= 10 ? "Region"
                : zoomLevel <= 18 ? "Place"
                : "Street";

            bool hasGeo = geocodingLevel switch
            {
                "Country" => locations.Any(l => !string.IsNullOrEmpty(l.Country)),
                "Region" => locations.Any(l => !string.IsNullOrEmpty(l.Region)),
                "Place" => locations.Any(l => !string.IsNullOrEmpty(l.Place)),
                "Street" => locations.Any(l => !string.IsNullOrEmpty(l.StreetName)),
                _ => false
            };

            if (hasGeo)
            {
                var geocoded = geocodingLevel switch
                {
                    "Country" => locations.Where(l => !string.IsNullOrEmpty(l.Country)).ToList(),
                    "Region" => locations.Where(l => !string.IsNullOrEmpty(l.Region)).ToList(),
                    "Place" => locations.Where(l => !string.IsNullOrEmpty(l.Place)).ToList(),
                    "Street" => locations.Where(l => !string.IsNullOrEmpty(l.StreetName)).ToList(),
                    _ => locations
                };
                var nonGeo = geocodingLevel switch
                {
                    "Country" => locations.Where(l => string.IsNullOrEmpty(l.Country)).ToList(),
                    "Region" => locations.Where(l => string.IsNullOrEmpty(l.Region)).ToList(),
                    "Place" => locations.Where(l => string.IsNullOrEmpty(l.Place)).ToList(),
                    "Street" => locations.Where(l => string.IsNullOrEmpty(l.StreetName)).ToList(),
                    _ => new List<Location>()
                };
                var quads = DivideMapIntoQuadrants(minLongitude, minLatitude, maxLongitude, maxLatitude);
                var sampled = SampleLocationsByQuadrants(nonGeo, quads, zoomLevel);
                locations = geocoded.Concat(sampled).ToList();
            }
            else
            {
                var quads = DivideMapIntoQuadrants(minLongitude, minLatitude, maxLongitude, maxLatitude);
                locations = SampleLocationsByQuadrants(locations, quads, zoomLevel).ToList();
            }

            var resultDtos = locations.Select(l => new PublicLocationDto
            {
                Id = l.Id,
                Coordinates = l.Coordinates,
                Timestamp = l.Timestamp,
                LocalTimestamp = CoordinateTimeZoneConverter
                    .ConvertUtcToLocal(
                        l.Coordinates.Y,
                        l.Coordinates.X,
                        DateTime.SpecifyKind(l.LocalTimestamp, DateTimeKind.Utc)),
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
                LocationTimeThresholdMinutes = locationTimeThreshold
            }).ToList();

            return (resultDtos, totalItems);
        }

        private async Task<List<Location>> GetSampledLocationsAsync(
            double minLon, double minLat,
            double maxLon, double maxLat,
            int precision, int limit,
            string userId, CancellationToken ct)
        {
            var sql = @"
        WITH ranked AS (
          SELECT
            ""Id"",
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
          JOIN ""public"".""Locations"" l
            ON l.""Id"" = r.""Id""
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


        private List<(double minX, double minY, double maxX, double maxY)> DivideMapIntoQuadrants(
            double minLongitude, double minLatitude,
            double maxLongitude, double maxLatitude)
        {
            var midX = (minLongitude + maxLongitude) / 2;
            var midY = (minLatitude + maxLatitude) / 2;

            return new List<(double, double, double, double)>
            {
                (minLongitude, minLatitude, midX, midY),
                (midX, minLatitude, maxLongitude, midY),
                (minLongitude, midY, midX, maxLatitude),
                (midX, midY, maxLongitude, maxLatitude)
            };
        }

        private IEnumerable<Location> SampleLocationsByQuadrants(
            List<Location> locations,
            List<(double minX, double minY, double maxX, double maxY)> quadrants,
            double zoomLevel)
        {
            int sampleSize = zoomLevel switch
            {
                <= 5 => 1,
                <= 10 => 3,
                _ => 5
            };

            var sampled = new List<Location>();
            foreach (var q in quadrants)
            {
                sampled.AddRange(
                    locations
                        .Where(l => IsWithinQuadrant(l.Coordinates.Coordinate, q))
                        .Take(sampleSize)
                );
            }

            return sampled;
        }

        private bool IsWithinQuadrant(
            Coordinate coord,
            (double minX, double minY, double maxX, double maxY) q)
            => coord.X >= q.minX && coord.X <= q.maxX
                                 && coord.Y >= q.minY && coord.Y <= q.maxY;

        /// <summary>
        /// Gets locations for a specific chronological period (day, month, or year).
        /// For single day: returns all unfiltered locations.
        /// For month/year: uses sampling to avoid overwhelming the frontend with 8500-21500+ locations.
        /// </summary>
        /// <param name="userId">User ID to filter locations</param>
        /// <param name="dateType">Type of period: "day", "month", or "year"</param>
        /// <param name="year">Year to filter</param>
        /// <param name="month">Month to filter (1-12, required if dateType is "month" or "day")</param>
        /// <param name="day">Day to filter (1-31, required if dateType is "day")</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of locations and total count</returns>
        public async Task<(List<PublicLocationDto> Locations, int TotalItems)> GetLocationsByDateAsync(
            string userId, string dateType, int year, int? month = null, int? day = null,
            CancellationToken cancellationToken = default)
        {
            // Load settings
            var settings = await _dbContext.ApplicationSettings.FirstOrDefaultAsync(cancellationToken);
            int locationTimeThreshold = settings?.LocationTimeThresholdMinutes ?? 10;

            // Build date range based on dateType
            DateTime startDate, endDate;
            switch (dateType.ToLower())
            {
                case "day":
                    if (!month.HasValue || !day.HasValue)
                        throw new ArgumentException("Month and day are required for day filter");
                    startDate = new DateTime(year, month.Value, day.Value, 0, 0, 0, DateTimeKind.Utc);
                    endDate = DateTime.SpecifyKind(startDate.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
                    break;

                case "month":
                    if (!month.HasValue)
                        throw new ArgumentException("Month is required for month filter");
                    startDate = new DateTime(year, month.Value, 1, 0, 0, 0, DateTimeKind.Utc);
                    endDate = DateTime.SpecifyKind(startDate.AddMonths(1).AddTicks(-1), DateTimeKind.Utc);
                    break;

                case "year":
                    startDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    endDate = DateTime.SpecifyKind(new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999), DateTimeKind.Utc);
                    break;

                default:
                    throw new ArgumentException($"Invalid dateType: {dateType}. Must be 'day', 'month', or 'year'");
            }

            // Count total items
            int totalItems = await _dbContext.Locations
                .Where(l => l.UserId == userId
                            && l.LocalTimestamp >= startDate
                            && l.LocalTimestamp <= endDate)
                .CountAsync(cancellationToken);

            // For single day, return all locations unfiltered
            if (dateType.ToLower() == "day")
            {
                var dayLocations = await _dbContext.Locations
                    .Where(l => l.UserId == userId
                                && l.LocalTimestamp >= startDate
                                && l.LocalTimestamp <= endDate)
                    .Include(l => l.ActivityType)
                    .OrderBy(l => l.LocalTimestamp)
                    .ToListAsync(cancellationToken);

                var dayDtos = dayLocations.Select(l => new PublicLocationDto
                {
                    Id = l.Id,
                    Coordinates = l.Coordinates,
                    Timestamp = l.Timestamp,
                    LocalTimestamp = CoordinateTimeZoneConverter
                        .ConvertUtcToLocal(
                            l.Coordinates.Y,
                            l.Coordinates.X,
                            DateTime.SpecifyKind(l.LocalTimestamp, DateTimeKind.Utc)),
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
                    LocationTimeThresholdMinutes = locationTimeThreshold
                }).ToList();

                return (dayDtos, totalItems);
            }

            // For month/year, use sampling to avoid overwhelming frontend
            List<Location> locations;

            // If small dataset, return all
            if (totalItems <= 500)
            {
                locations = await _dbContext.Locations
                    .Where(l => l.UserId == userId
                                && l.LocalTimestamp >= startDate
                                && l.LocalTimestamp <= endDate)
                    .Include(l => l.ActivityType)
                    .OrderBy(l => l.LocalTimestamp)
                    .ToListAsync(cancellationToken);
            }
            else
            {
                // Use country-based sampling for large datasets
                var countrySql = @"
                    WITH ranked AS (
                      SELECT *,
                             ROW_NUMBER() OVER (
                               PARTITION BY ""Country""
                               ORDER BY ""LocalTimestamp"" DESC
                             ) AS rn
                        FROM ""public"".""Locations""
                       WHERE ""UserId"" = @userId
                         AND ""LocalTimestamp"" >= @startDate
                         AND ""LocalTimestamp"" <= @endDate
                         AND ""Country"" IS NOT NULL
                    )
                    SELECT * FROM ranked WHERE rn <= 5
                ";

                var countryParams = new[]
                {
                    new NpgsqlParameter("userId", userId),
                    new NpgsqlParameter("startDate", startDate),
                    new NpgsqlParameter("endDate", endDate)
                };

                var countryBatch = await _dbContext.Locations
                    .FromSqlRaw(countrySql, countryParams)
                    .Include(l => l.ActivityType)
                    .ToListAsync(cancellationToken);

                // Get additional sampled locations using geohash
                var geohashSql = @"
                    WITH ranked AS (
                      SELECT
                        ""Id"",
                        ROW_NUMBER() OVER (
                          PARTITION BY ST_GeoHash((""Coordinates""::geometry), 4)
                          ORDER BY ""LocalTimestamp"" DESC
                        ) AS rn
                      FROM ""public"".""Locations""
                      WHERE ""UserId"" = @userId
                        AND ""LocalTimestamp"" >= @startDate
                        AND ""LocalTimestamp"" <= @endDate
                    )
                    SELECT l.*
                      FROM ranked r
                      JOIN ""public"".""Locations"" l ON l.""Id"" = r.""Id""
                     WHERE r.rn = 1
                     LIMIT 1000
                ";

                var geohashParams = new[]
                {
                    new NpgsqlParameter("userId", userId),
                    new NpgsqlParameter("startDate", startDate),
                    new NpgsqlParameter("endDate", endDate)
                };

                var sampledLocations = await _dbContext.Locations
                    .FromSqlRaw(geohashSql, geohashParams)
                    .Include(l => l.ActivityType)
                    .ToListAsync(cancellationToken);

                // Merge and deduplicate
                var pickedIds = countryBatch.Select(l => l.Id).ToHashSet();
                locations = countryBatch
                    .Concat(sampledLocations.Where(l => !pickedIds.Contains(l.Id)))
                    .ToList();
            }

            var resultDtos = locations.Select(l => new PublicLocationDto
            {
                Id = l.Id,
                Coordinates = l.Coordinates,
                Timestamp = l.Timestamp,
                LocalTimestamp = CoordinateTimeZoneConverter
                    .ConvertUtcToLocal(
                        l.Coordinates.Y,
                        l.Coordinates.X,
                        DateTime.SpecifyKind(l.LocalTimestamp, DateTimeKind.Utc)),
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
                LocationTimeThresholdMinutes = locationTimeThreshold
            }).ToList();

            return (resultDtos, totalItems);
        }

        /// <summary>
        /// Checks if user has location data for a specific date.
        /// Used for conditional prev/next date navigation.
        /// </summary>
        /// <param name="userId">User ID</param>
        /// <param name="date">Date to check</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if data exists for the date</returns>
        public async Task<bool> HasDataForDateAsync(string userId, DateTime date, CancellationToken cancellationToken = default)
        {
            var startDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            var endDate = DateTime.SpecifyKind(startDate.AddDays(1).AddTicks(-1), DateTimeKind.Utc);

            return await _dbContext.Locations
                .AnyAsync(l => l.UserId == userId
                               && l.LocalTimestamp >= startDate
                               && l.LocalTimestamp <= endDate,
                    cancellationToken);
        }

        /// <summary>
        /// Checks if user has location data for a specific month.
        /// </summary>
        /// <param name="userId">User ID</param>
        /// <param name="year">Year</param>
        /// <param name="month">Month (1-12)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if data exists for the month</returns>
        public async Task<bool> HasDataForMonthAsync(string userId, int year, int month, CancellationToken cancellationToken = default)
        {
            var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = DateTime.SpecifyKind(startDate.AddMonths(1).AddTicks(-1), DateTimeKind.Utc);

            return await _dbContext.Locations
                .AnyAsync(l => l.UserId == userId
                               && l.LocalTimestamp >= startDate
                               && l.LocalTimestamp <= endDate,
                    cancellationToken);
        }

        /// <summary>
        /// Checks if user has location data for a specific year.
        /// </summary>
        /// <param name="userId">User ID</param>
        /// <param name="year">Year</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if data exists for the year</returns>
        public async Task<bool> HasDataForYearAsync(string userId, int year, CancellationToken cancellationToken = default)
        {
            var startDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = DateTime.SpecifyKind(new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999), DateTimeKind.Utc);

            return await _dbContext.Locations
                .AnyAsync(l => l.UserId == userId
                               && l.LocalTimestamp >= startDate
                               && l.LocalTimestamp <= endDate,
                    cancellationToken);
        }
    }
}