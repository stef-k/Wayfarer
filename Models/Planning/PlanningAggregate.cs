using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wayfarer.Models.Planning;

/// <summary>One owner-authored, ordered collection; null storage means no planning.</summary>
public sealed record PlanningAggregate(int Format, IReadOnlyList<PlanningEntry> Entries);

/// <summary>A stable local intention, without inferred participation, meaning or duration.</summary>
public sealed record PlanningEntry(Guid Id, PlanningTiming When, string? Label = null, string? Kind = null, int? AllocationMinutes = null);

/// <summary>Exclusive At, period, unspecified, unknown or flexible timing.</summary>
public sealed record PlanningTiming(string Form, PlanningValue? Value = null, PlanningBound? Start = null, PlanningBound? End = null);

/// <summary>A missing bound remains distinct from an explicit unknown or flexible bound.</summary>
public sealed record PlanningBound(string State, PlanningValue? Value = null);

/// <summary>Literal Gregorian civil components and only explicitly authored zone/occurrence choices.</summary>
public sealed record PlanningValue(string Precision, string Local, string? Zone = null, int? SelectedOffsetSeconds = null);

/// <summary>Immediate mapped boundary shared by the five supported graph entity types.</summary>
public interface IPlanningTarget
{
    /// <summary>Private typed content, excluded from raw entity serialization and form binding.</summary>
    PlanningAggregate? Planning { get; set; }
}

/// <summary>One strict codec for persistence, protected originals and the separately owned v4 adapter.</summary>
public static class PlanningCodec
{
    /// <summary>Rejects duplicate/unsupported members rather than silently losing authored content.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    /// <summary>Reads typed structure without applying today's zone rules to historical stored values.</summary>
    public static PlanningAggregate Read(string json) => JsonSerializer.Deserialize<PlanningAggregate>(json, Json)
        ?? throw new JsonException("Planning must be an aggregate.");

    /// <summary>Writes all authored qualifiers in stable typed field order.</summary>
    public static string Write(PlanningAggregate value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Compares typed values including entry order and absence, independent of wire property order.</summary>
    public static bool Equal(PlanningAggregate? left, PlanningAggregate? right) =>
        left == null || right == null ? left == right : Write(left) == Write(right);
}
