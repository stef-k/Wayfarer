using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Text.Json.Serialization;

namespace Wayfarer.Models;

/// <summary>Internal rebuildable history facts; never owner/import input or entity publication.</summary>
public partial class Location
{
    /// <summary>Coordinate-derived wall time under HistoryTimeVersion, never an original device clock.</summary>
    [JsonIgnore, BindNever] public DateTime? CaptureLocalTimestamp { get; internal set; }
    /// <summary>IANA coordinate mapping used for the derived wall time.</summary>
    [JsonIgnore, BindNever] public string? CaptureTimeZoneId { get; internal set; }
    /// <summary>Historical rules offset at the canonical UTC event.</summary>
    [JsonIgnore, BindNever] public int? CaptureOffsetSeconds { get; internal set; }
    /// <summary>Converter, coordinate dataset and historical rules identity; null means pending.</summary>
    [JsonIgnore, BindNever] public string? HistoryTimeVersion { get; internal set; }
    /// <summary>Null pending, resolved, or terminal exceptional unresolved evidence.</summary>
    [JsonIgnore, BindNever] public string? HistoryTimeStatus { get; internal set; }
    /// <summary>Safe evidence category, never an operational exception or private content.</summary>
    [JsonIgnore, BindNever] public string? HistoryTimeFailureCode { get; internal set; }
    /// <summary>Independently normalized visible fields, separated by newline.</summary>
    [JsonIgnore, BindNever] public string? HistorySearchText { get; internal set; }
    /// <summary>Extraction, accent folding and runtime Unicode rules identity; null means pending.</summary>
    [JsonIgnore, BindNever] public string? HistoryTextVersion { get; internal set; }
}
