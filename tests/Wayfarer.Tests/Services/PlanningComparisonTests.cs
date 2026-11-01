using Wayfarer.Models.Planning;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Product comparison witnesses at the shared value seam; no browser or relational matrix.</summary>
public class PlanningComparisonTests
{
    private static PlanningValue Clock(string clock, string? zone = "UTC", int? offset = null) => new("dateTime", "2027-03-02T" + clock, zone, offset);
    private static PlanningEntry Period(string start, string end, string kind = "presence") => new(Guid.NewGuid(), new("period", Start: new("known", Clock(start)), End: new("known", Clock(end))), Kind: kind);
    private static PlanningPlacements Range(string start, string end) => PlanningIntervals.Resolve(Period(start, end));

    [Theory]
    [InlineData("11:30", 30)]
    [InlineData("12:30", 60)]
    public void Containment_counts_complete_outside_coverage_and_preserves_union_gap(string end, double outside)
    {
        var windows = end == "12:30" ? new[] { Range("10:00", "11:00"), Range("12:00", "13:00") } : [Range("10:00", "11:00")];
        var result = PlanningComparisons.Containment(Range("10:30", end), windows);
        Assert.Equal("mismatch", result.Status);
        Assert.Equal(outside, result.Minutes);
        Assert.Equal("completed", PlanningComparisons.Containment(Range("10:35", "10:45"), windows).Status);
    }

    [Fact]
    public void Union_does_not_double_count_or_fill_gaps_and_moments_respect_exclusive_end()
    {
        Assert.Equal("completed", PlanningComparisons.Containment(Range("10:15", "12:30"), [Range("10:00", "12:00"), Range("11:00", "13:00")]).Status);
        Assert.Equal("mismatch", PlanningComparisons.Containment(Range("11:00", "11:00"), [Range("10:00", "11:00")]).Status);
        Assert.Equal("inconsistent", PlanningComparisons.Containment(Range("10:00", "10:30"), [Range("12:00", "11:00")]).Status);
        Assert.Equal("unknown", PlanningComparisons.Containment(Range("10:00", "10:30"), []).Status);
    }

    [Fact]
    public void Fold_retains_possible_containment_and_zone_gap_stays_unknown()
    {
        var child = new PlanningEntry(Guid.NewGuid(), new("at", new("dateTime", "2027-10-31T01:30", "Europe/London")), Kind: "presence", AllocationMinutes: 15);
        var parent = new PlanningEntry(Guid.NewGuid(), new("period", Start: new("known", new("dateTime", "2027-10-31T00:00", "UTC")), End: new("known", new("dateTime", "2027-10-31T01:00", "UTC"))), Kind: "window");
        var result = PlanningComparisons.Containment(PlanningIntervals.Resolve(child), [PlanningIntervals.Resolve(parent)]);
        Assert.Equal("possible", result.Status);
        Assert.Null(result.Minutes);
        var gap = child with { When = new("at", new("dateTime", "2027-03-28T01:30", "Europe/London")) };
        Assert.Equal("unknown", PlanningComparisons.Containment(PlanningIntervals.Resolve(gap), [PlanningIntervals.Resolve(parent)]).Status);
        Assert.Equal("unknown", PlanningIntervals.Resolve(child with { When = new("at", Clock("10:00", null)) }).Problem == null ? "completed" : "unknown");
    }

    [Fact]
    public void Coarse_window_uses_inclusive_granules_but_coarse_presence_is_not_occupation()
    {
        var window = new PlanningEntry(Guid.NewGuid(), new("period", Start: new("known", new("date", "2027-03-01")), End: new("known", new("date", "2027-03-10"))), Kind: "window");
        var child = new PlanningEntry(Guid.NewGuid(), new("at", new("date", "2027-03-10")), Kind: "window");
        Assert.Equal("completed", PlanningComparisons.Containment(PlanningIntervals.Resolve(child), [PlanningIntervals.Resolve(window)]).Status);
        Assert.NotNull(PlanningIntervals.Resolve(child with { Kind = "presence" }).Problem);
        Assert.Equal("unknown", PlanningComparisons.Containment(Range("10:00", "11:00"), [PlanningIntervals.Resolve(window)]).Status);
        Assert.Equal("completed", PlanningComparisons.Containment(Range("10:00", "11:00"), [PlanningIntervals.Resolve(window, "UTC")]).Status);
    }

    [Fact]
    public void Physical_conclusion_requires_both_premises_and_adjacency_is_not_overlap()
    {
        Assert.Equal("unknown", PlanningComparisons.Physical(Range("10:00", "11:00"), Range("10:30", "11:30"), false, true).Status);
        Assert.Equal("unknown", PlanningComparisons.Physical(Range("10:00", "11:00"), Range("10:30", "11:30"), true, false).Status);
        Assert.Equal("contradiction", PlanningComparisons.Physical(Range("10:00", "11:00"), Range("10:30", "11:30"), true, true).Status);
        Assert.Equal("completed", PlanningComparisons.Physical(Range("10:00", "11:00"), Range("11:00", "12:00"), true, true).Status);
    }

    [Fact]
    public void Alice_missing_estimate_shortfall_negative_gap_and_included_buffers_remain_distinct()
    {
        var release = PlanningIntervals.Endpoint(Period("10:00", "11:00"), true);
        var arrival = PlanningIntervals.Endpoint(Period("12:00", "13:00"), false);
        var alice = Assert.Single(PlanningComparisons.Movement(release, arrival, 30, 15, true));
        Assert.Equal("completed", alice.Status);
        Assert.Equal(15, alice.Minutes);
        Assert.Equal("unknown", Assert.Single(PlanningComparisons.Movement(release, arrival, null, 0, true)).Status);
        var risk = PlanningComparisons.Movement(release, PlanningIntervals.Endpoint(Period("13:00", "14:00"), false), 180, null, false);
        Assert.Equal(60, risk[0].Minutes);
        Assert.Equal("risk", risk[0].Status);
        Assert.Equal("unknown", risk[1].Status);
        Assert.Equal("contradiction", Assert.Single(PlanningComparisons.Movement(arrival, release, null, null, false)).Status);
        Assert.Equal(30, Assert.Single(PlanningComparisons.Movement(release, arrival, 30, 0, true)).Minutes);
        var point = new PlanningEntry(Guid.NewGuid(), new("at", Clock("11:00")));
        Assert.NotNull(PlanningIntervals.Endpoint(point, true).Problem);
        Assert.NotNull(PlanningIntervals.Endpoint(new(Guid.NewGuid(), new("period", End: new("known", Clock("12:00")))), false).Problem);
    }
}
