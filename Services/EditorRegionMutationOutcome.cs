namespace Wayfarer.Services;

/// <summary>
/// Service-level outcome that lets the controller keep HTTP status orchestration.
/// </summary>
public sealed record EditorRegionMutationOutcome<T>(
    EditorRegionMutationStatus Status,
    T? Result,
    Dictionary<string, string[]>? ValidationErrors,
    string? ForbiddenDetail)
{
    /// <summary>Creates a successful mutation outcome.</summary>
    public static EditorRegionMutationOutcome<T> Succeeded(T result) =>
        new(EditorRegionMutationStatus.Success, result, null, null);

    /// <summary>Creates a not-found outcome for hidden or missing trips/entities.</summary>
    public static EditorRegionMutationOutcome<T> NotFound() =>
        new(EditorRegionMutationStatus.NotFound, default, null, null);

    /// <summary>Creates a forbidden outcome for valid but blocked region operations.</summary>
    public static EditorRegionMutationOutcome<T> Forbidden(string detail) =>
        new(EditorRegionMutationStatus.Forbidden, default, null, detail);

    /// <summary>Creates a validation-failed outcome with field-keyed errors.</summary>
    public static EditorRegionMutationOutcome<T> ValidationFailed(Dictionary<string, string[]> errors) =>
        new(EditorRegionMutationStatus.ValidationFailed, default, errors, null);
}

/// <summary>
/// Non-success service outcomes for Trip Editor region mutations.
/// </summary>
public enum EditorRegionMutationStatus
{
    /// <summary>The mutation succeeded and produced a result envelope.</summary>
    Success,

    /// <summary>The ownership-filtered trip or target entity was not found.</summary>
    NotFound,

    /// <summary>The target exists but the operation is forbidden.</summary>
    Forbidden,

    /// <summary>The request payload failed deterministic validation.</summary>
    ValidationFailed
}
