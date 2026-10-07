using System.ComponentModel.DataAnnotations;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.ConnectionModels;

/// <summary>Safe personal-connection metadata, without credential entities or verifier values.</summary>
public sealed record ConnectionTokenViewModel(string UserName, ConnectionTokenStatus? Status, bool IsHttps);

/// <summary>An observed canonical row state and explicit consent to reconnect apps.</summary>
public sealed class ReplaceConnectionTokenInput
{
    /// <summary>Canonical row identity, always matched within the authenticated owner.</summary>
    [Range(1, int.MaxValue)]
    public int TokenId { get; set; }

    /// <summary>The exact UTC issuance state previously displayed by the safe GET.</summary>
    [Required]
    public DateTime? IssuedAt { get; set; }

    /// <summary>Confirms that apps using the previous credential must reconnect.</summary>
    public bool Confirmed { get; set; }
}
