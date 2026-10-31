using System.Text.Json;
using Wayfarer.Models.Planning;
using Wayfarer.Services;

namespace Wayfarer.Models.Dtos;

/// <summary>One explicit Set/Clear against the complete original aggregate and original direct parent.</summary>
internal sealed record PlanningMutationRequest(Guid ParentId, PlanningAggregate? Original, PlanningAggregate? Value)
{
    /// <summary>Parses exact originals, including explicit absence; null input never acts as Clear.</summary>
    internal static async Task<PlanningMutationRequest> ReadAsync(Stream body, CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 16 }, token);
            var fields = PlaceStructuralRequest.Fields(document.RootElement, "expected", "operation", "value");
            var expected = PlaceStructuralRequest.Fields(fields.GetValueOrDefault("expected"), "parentId", "planning");
            if (expected.Count != 2 || !expected["parentId"].TryGetGuid(out var parent) || parent == Guid.Empty) throw Invalid();
            var original = expected["planning"].ValueKind == JsonValueKind.Null ? null : PlanningCodec.Read(expected["planning"].GetRawText());
            var operation = fields.GetValueOrDefault("operation").GetString();
            if (operation == "clear" && !fields.ContainsKey("value")) return new(parent, original, null);
            if (operation != "set" || !fields.TryGetValue("value", out var value) || value.ValueKind != JsonValueKind.Object) throw Invalid();
            return new(parent, original, PlanningCodec.Read(value.GetRawText()));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw Invalid(); }
    }

    /// <summary>Safe known-unapplied error, without submitted or current private content.</summary>
    private static TripMetadataMutationRejected Invalid() => new(400, "planning-request-invalid", "Provide Set or Clear and complete planning originals.",
        new() { ["planning"] = ["A valid operation, original parent and complete original planning (or explicit null) are required."] });
}
