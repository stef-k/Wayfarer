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
        writer.WriteStringValue(ObservationEvidence.Format(value));
}

/// <summary>Normalizes explicit capture instants before DateTime binding, retaining legacy coordinate-local input.</summary>
public sealed class CaptureTimestampJsonConverter : JsonConverter<DateTime>
{
    /// <summary>Explicit offsets use shared precision; offsetless legacy clocks retain wall-time interpretation.</summary>
    public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Invalid capture time.");
        var text = reader.GetString()!;
        if (ObservationEvidence.ReadInstant(text) is { } instant)
        {
            // Binding explicit instants as UTC avoids host-zone clamping at finite calendar boundaries.
            return instant.UtcDateTime;
        }
        if (text.EndsWith('Z') || text.Length > 19 && (text[^6] is '+' or '-'))
            throw new JsonException("Invalid capture instant.");
        if (!ObservationEvidence.ValidOriginalLocal(text)) throw new JsonException("Invalid capture time.");
        var fraction = text.Length > 19 ? "." + text[20..].PadRight(6, '0')[..6] : "";
        return DateTime.ParseExact(text[..19] + fraction, ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.ffffff"],
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
    }

    /// <summary>Uses the shared instant representation on output.</summary>
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Kind == DateTimeKind.Unspecified
            ? value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture)
            : ObservationEvidence.Format(new DateTimeOffset(value.ToUniversalTime())));
}
