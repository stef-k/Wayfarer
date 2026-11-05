using System.Text.Json;

namespace Wayfarer.Services;

/// <summary>Faithful render-only islands with explicit vertex/island/UTF-8 budgets; overflow discards the whole path.</summary>
internal sealed class TimelinePathGeometry
{
    // Focused routed calibration is recorded in docs/development/timeline-calendar.md; these are drawing budgets only.
    internal const int MaximumVertices = 20000;
    internal const int MaximumIslands = 1024;
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal bool Available { get; private set; } = true;
    internal List<List<double[]>> Islands { get; } = [];
    private List<double[]>? _current;
    private int _vertices;
    private int _bytes = 2;

    /// <summary>No edge may bridge a group/policy barrier.</summary>
    internal void Break() => _current = null;

    /// <summary>Seam vertices are display-only; no synthetic event/time or analytical edge is introduced.</summary>
    internal void AddEdge(TimelineSourceRow a, TimelineSourceRow b)
    {
        if (!Available) return;
        if (_current == null) { Start(); Append(a.Longitude!.Value, a.Latitude!.Value); }
        var x = a.Longitude!.Value; var y = b.Longitude!.Value;
        if (Math.Abs(y - x) > 180)
        {
            var unwrapped = y + (x > 0 ? 360 : -360);
            var seam = x > 0 ? 180 : -180;
            var latitude = a.Latitude!.Value + (b.Latitude!.Value - a.Latitude.Value) * ((seam - x) / (unwrapped - x));
            Append(seam, latitude); Start(); Append(-seam, latitude);
        }
        Append(y, b.Latitude!.Value);
    }

    /// <summary>Account for brackets/commas and islands, including the two halves of a dateline crossing.</summary>
    private void Start()
    {
        if (!Available) return;
        if (Islands.Count == MaximumIslands || _bytes + 3 > MaximumBytes) { Overflow(); return; }
        _bytes += Islands.Count == 0 ? 2 : 3;
        _current = []; Islands.Add(_current);
    }

    /// <summary>Measure actual serialized vertex bytes before retaining it, avoiding a plausible truncated result.</summary>
    private void Append(double longitude, double latitude)
    {
        if (!Available) return;
        double[] point = [longitude, latitude];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(point).Length + (_current!.Count == 0 ? 0 : 1);
        if (_vertices == MaximumVertices || _bytes + bytes > MaximumBytes) { Overflow(); return; }
        _vertices++; _bytes += bytes; _current.Add(point);
    }

    /// <summary>Release all retained geometry and stop future retention while the analytical reducer completes.</summary>
    private void Overflow() { Available = false; Islands.Clear(); _current = null; }
}
