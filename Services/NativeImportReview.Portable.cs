using System.Text.Json;
using Wayfarer.Models;

namespace Wayfarer.Services;

internal sealed partial class NativeImportReviewProtection
{
    /// <summary>Portable fields share current-content-bound permits; omission never silently removes inherited obligations.</summary>
    private IEnumerable<NativeImportPlanningRow> PortableRows(Trip current, Trip incoming, string binding)
    {
        if (current.ContributionPolicy != null || incoming.ContributionPolicy != null || current.Credits.Count > 0 || incoming.Credits.Count > 0)
            yield return PortableRow("credits", "trip", current.Name, "retained", Terms(current), Terms(incoming), binding);
        var next = incoming.Segments.ToDictionary(s => s.Id);
        foreach (var segment in current.Segments)
        {
            var retained = next.Remove(segment.Id, out var target);
            if (segment.Title == null && target?.Title == null) continue;
            yield return PortableRow($"title/{segment.Id:D}", "segment", segment.Title ?? segment.Mode,
                retained ? "retained" : "removed", segment.Title, target?.Title, binding);
        }
        foreach (var segment in next.Values.Where(s => s.Title != null))
            yield return PortableRow($"title/{segment.Id:D}", "segment", segment.Title!, "new", null, segment.Title, binding);
    }

    private NativeImportPlanningRow PortableRow(string key, string kind, string name, string state, string? before, string? after, string binding)
    {
        state = state is "removed" or "new" ? state : before == after ? "equal" : "conflict";
        string[] actions = state == "removed" ? ["remove"] : state == "new" ? []
            : state == "equal" ? ["keep", "clear"] : ["keep", "use", "clear"];
        return new(key, kind, name, state, null, null,
            actions.Select(a => new NativeImportChoice(a, Issue(binding, key, a))).ToArray(),
            key == "credits" ? "credits / contribution terms" : "title", before, after);
    }

    /// <summary>All permits have already been checked. Default Keep preserves root terms and retained title originals.</summary>
    private static void ApplyPortable(Trip current, Trip incoming, IReadOnlyDictionary<string, string> selected)
    {
        var terms = selected.GetValueOrDefault("credits");
        if (terms == "clear") { incoming.Credits = []; incoming.ContributionPolicy = null; }
        else if (terms != "use") { incoming.Credits = current.Credits; incoming.ContributionPolicy = current.ContributionPolicy; }
        var prior = current.Segments.ToDictionary(s => s.Id);
        foreach (var segment in incoming.Segments)
        {
            if (!prior.TryGetValue(segment.Id, out var original)) continue;
            var action = selected.GetValueOrDefault($"title/{segment.Id:D}");
            if (action == "clear") segment.Title = null;
            else if (action != "use") segment.Title = original.Title;
        }
    }

    private static string Terms(Trip trip) => JsonSerializer.Serialize(new { trip.ContributionPolicy, trip.Credits });
}
