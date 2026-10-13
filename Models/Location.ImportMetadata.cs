using System.ComponentModel.DataAnnotations.Schema;

namespace Wayfarer.Models;

/// <summary>
/// Import-time metadata for <see cref="Location"/> that should not be persisted.
/// </summary>
public partial class Location
{
    /// <summary>
    /// Holds the activity name parsed from an import so that the service layer can resolve it.
    /// </summary>
    [NotMapped]
    public string? ImportedActivityName { get; set; }

    /// <summary>Marks an invalid supplied Activity value without persisting its private source text.</summary>
    [NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool ImportedActivityInvalid { get; set; }
}
