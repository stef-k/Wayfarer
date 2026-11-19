using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

/// <summary>Updates the stored personal sharing preference.</summary>
public class OrgPeerVisibilityAccessRequest
{
    private bool _disabled;

    /// <summary>The released scalar request defaults to false when the field is absent.</summary>
    public bool Disabled
    {
        get => _disabled;
        set
        {
            _disabled = value;
            HasDisabled = true;
        }
    }

    /// <summary>Presence, including an explicit null, selects the mobile reference-bearing contract.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement MembershipRef { get; set; }

    /// <summary>Reference-bearing writes require the boolean to be explicitly supplied.</summary>
    internal bool HasDisabled { get; private set; }
}
