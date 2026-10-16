namespace Wayfarer.Parsers;

/// <summary>Carries only fixed, privacy-safe identity diagnostics from native parsers to the fenced worker.</summary>
internal sealed class LocationImportValidationException : FormatException
{
    /// <summary>Identifies the input failure without retaining any supplied value or file information.</summary>
    internal string Code { get; }

    private LocationImportValidationException(string code, string message) : base(message) => Code = code;

    /// <summary>Rejects a supplied identity that cannot be parsed as a GUID string.</summary>
    internal static LocationImportValidationException InvalidKey() => new(
        "location-import-invalid-idempotency-key",
        "Invalid IdempotencyKey. Correct UUID values and upload the corrected file as a new import.");

    /// <summary>Rejects ambiguous repeated zero keys while retaining singleton historical compatibility.</summary>
    internal static LocationImportValidationException MultipleZeroKeys() => new(
        "location-import-multiple-zero-idempotency-keys",
        "Multiple zero IdempotencyKeys. Give distinct captures unique nonzero UUIDs and upload the corrected file as a new import.");
}
