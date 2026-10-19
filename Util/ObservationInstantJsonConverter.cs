using System.Text.Json;
using System.Text.Json.Serialization;
using Wayfarer.Parsers;

namespace Wayfarer.Util;

/// <summary>Requires explicit fix-clock offsets without changing legacy event timestamp binding.</summary>
public sealed class ObservationInstantJsonConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>Rejects malformed/unspecified new clocks through the existing MVC JSON error boundary.</summary>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && ObservationEvidence.ReadInstant(reader.GetString()) is { } instant
            ? instant : throw new JsonException("Observation instant requires an explicit UTC or numeric offset.");

    /// <summary>Writes the original instant normalized to UTC, preserving supported nullable DTO serialization.</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime());
}
