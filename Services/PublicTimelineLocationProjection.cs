using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Request-scoped public Location source shared by points, latest selection and summaries.</summary>
public sealed class PublicTimelineLocationProjection
{
    private readonly string _userId;
    private readonly DateTime? _cutoff;

    /// <summary>Resolves visibility and the inclusive UTC event-time cutoff exactly once.</summary>
    public PublicTimelineLocationProjection(ApplicationUser user, DateTime utcNow)
    {
        var eligibility = PublicTimelineEligibilityResolver.Resolve(user);
        IsAvailable = eligibility.IsEffectivelyPublic;
        _userId = user.Id;
        _cutoff = eligibility.IsEffectivelyPublic && !eligibility.IsLive
            ? utcNow.Subtract(eligibility.Delay!.Value) : null;
    }

    /// <summary>False for private or invalid persisted settings; the SQL also fails closed.</summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// Filters persisted UTC client event time, before display conversion or sampling.
    /// Geometry ST_Contains preserves polygon interior semantics (boundary points remain eligible).
    /// </summary>
    public const string SourceSql = """
        (SELECT location.* FROM "public"."Locations" location
         WHERE location."UserId" = @publicUser AND @publicAvailable
           AND (@publicCutoff IS NULL OR location."LocalTimestamp" <= @publicCutoff)
           AND NOT EXISTS (SELECT 1 FROM "HiddenAreas" hidden
               WHERE hidden."UserId" = @publicUser AND hidden."Area" IS NOT NULL
                 AND ST_Contains(hidden."Area"::geometry, location."Coordinates"::geometry)))
        """;

    /// <summary>Creates fresh provider parameters for each command, retaining caller parameters.</summary>
    public object[] Bind(params object[] parameters) => parameters.Concat(new object[]
    {
        new NpgsqlParameter("publicUser", _userId),
        new NpgsqlParameter("publicAvailable", IsAvailable),
        new NpgsqlParameter("publicCutoff", NpgsqlDbType.TimestampTz) { Value = (object?)_cutoff ?? DBNull.Value }
    }).ToArray();

    /// <summary>Exposes the same eligible source for global latest-public-location selection.</summary>
    public IQueryable<Location> Query(ApplicationDbContext db) =>
        db.Locations.FromSqlRaw("SELECT * FROM " + SourceSql + " AS public_locations", Bind());
}
