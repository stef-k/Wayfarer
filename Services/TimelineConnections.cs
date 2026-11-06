namespace Wayfarer.Services;

/// <summary>Compact event-ordered source row. False/null membership and missing positions remain adjacency barriers.</summary>
internal readonly record struct TimelineSourceRow(int Id, DateTime EventUtc, bool? Matches, double? Longitude, double? Latitude)
{
    internal bool Qualified => Matches == true && Longitude is >= -180 and < 180 && Latitude is >= -90 and <= 90;
}

/// <summary>Complete observation facts; geometry and coverage are independent of source freshness.</summary>
internal sealed record TimelineConnectionResult(string Coverage, double? DistanceMetres, long EdgeCount,
    long SourceRowCount, long KnownMatchCount, long MappedMatchCount, long UnresolvedPlaceableCandidateCount,
    long EqualTimeDisagreementGroups, long GapBreaks, long SpeedBreaks, string GeometryState,
    IReadOnlyList<List<double[]>>? Geometry);

/// <summary>Single-pass reducer with constant equal-time/coincidence state, separate bounded optional drawing.</summary>
internal sealed class TimelineConnections(TimelineConnectionRules rules, bool geometry)
{
    internal const double RadiusMetres = 6371008.8;
    private readonly TimelinePathGeometry? _drawing = geometry ? new() : null;
    private TimelineSourceRow? _group;
    private TimelineSourceRow? _previous;
    private bool _groupQualified;
    private bool _missing;
    private bool _disagreement;
    private double _sum;
    private double _compensation;
    private long _rows, _matches, _mapped, _candidates, _edges, _disagreements, _gaps, _speeds;

    /// <summary>Retain every input decision, closing the whole tie before permitting either touching edge.</summary>
    internal void Add(TimelineSourceRow row)
    {
        if (_group is { } group && row.EventUtc < group.EventUtc) throw new InvalidOperationException("Unordered timeline source.");
        _rows++;
        if (row.Matches == null) { _candidates++; _missing = true; }
        if (row.Matches == true)
        {
            _matches++;
            if (row.Qualified) _mapped++; else _missing = true;
        }
        if (_group is { } current && current.EventUtc == row.EventUtc)
        {
            if (current.Longitude != row.Longitude || current.Latitude != row.Latitude) _disagreement = true;
            _groupQualified &= row.Qualified && current.Longitude == row.Longitude && current.Latitude == row.Latitude;
            return;
        }
        CloseGroup();
        _group = row; _groupQualified = row.Qualified; _disagreement = false;
    }

    /// <summary>Unplaceable time prevents all adjacency claims; partial islands do not manufacture a qualified zero.</summary>
    internal TimelineConnectionResult Finish(bool unplaceable)
    {
        CloseGroup(); _group = null;
        var coverage = unplaceable ? "unknown" : _missing ? "partial" : "complete";
        var distance = unplaceable || _missing && _edges == 0 ? (double?)null : _sum;
        var state = !geometry ? "not-requested" : unplaceable ? "unavailable" : _drawing!.Available ? "available" : "unavailable";
        return new(coverage, distance, unplaceable ? 0 : _edges, _rows, _matches, _mapped, _candidates,
            _disagreements, _gaps, _speeds, state, state == "available" ? _drawing!.Islands : null);
    }

    /// <summary>A failed group or policy edge breaks drawing; later qualified islands remain independently useful.</summary>
    private void CloseGroup()
    {
        if (_group is not { } current) return;
        if (_disagreement) _disagreements++;
        if (!_groupQualified) { _previous = null; _drawing?.Break(); return; }
        if (_previous is { } previous)
        {
            var seconds = (current.EventUtc.Ticks - previous.EventUtc.Ticks) / (double)TimeSpan.TicksPerSecond;
            var distance = Distance(previous, current);
            if (seconds <= 0 || seconds > rules.MaximumGapSeconds) { _gaps++; _drawing?.Break(); }
            else if (rules.MaximumSpeedMetresPerSecond is { } ceiling && distance / seconds > ceiling)
            { _speeds++; _drawing?.Break(); }
            else
            {
                // Compensated addition keeps small positive original edges; no geometry reduction enters the sum.
                var adjusted = distance - _compensation;
                var next = _sum + adjusted;
                if (!double.IsFinite(next)) throw new HistoryRequestException("calculationUnavailable", 503);
                _compensation = (next - _sum) - adjusted; _sum = next; _edges++;
                _drawing?.AddEdge(previous, current);
            }
        }
        _previous = current;
    }

    /// <summary>Shortest great-circle distance with bounded radians and clamped floating-point roundoff.</summary>
    internal static double Distance(TimelineSourceRow a, TimelineSourceRow b)
    {
        const double radians = Math.PI / 180;
        var latA = a.Latitude!.Value * radians; var latB = b.Latitude!.Value * radians;
        var deltaLatitude = (b.Latitude.Value - a.Latitude.Value) * radians;
        var deltaLongitude = Math.IEEERemainder(b.Longitude!.Value - a.Longitude!.Value, 360) * radians;
        var halfLat = Math.Sin(deltaLatitude / 2); var halfLon = Math.Sin(deltaLongitude / 2);
        var square = Math.Clamp(halfLat * halfLat + Math.Cos(latA) * Math.Cos(latB) * halfLon * halfLon, 0, 1);
        return 2 * RadiusMetres * Math.Atan2(Math.Sqrt(square), Math.Sqrt(1 - square));
    }
}
