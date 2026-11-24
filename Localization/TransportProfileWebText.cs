namespace Wayfarer.Localization;

/// <summary>Adapts reconciliation diagnostics for the Admin Transport Profile form.</summary>
internal static class TransportProfileWebText
{
    /// <summary>Preserves known failure distinctions and gives other dependency failures a bounded explanation.</summary>
    internal static string ReconciliationError(WebMessages text, string error) => text[error switch
    {
        "Planning speed must be a finite positive number or null." => "Catalogs.Validation.SpeedOrEmpty",
        "Transport profile was not found." => "Catalogs.TransportNotFound",
        "The profile changed after the form was loaded." => "Catalogs.TransportStale",
        "A referenced Segment changed concurrently." => "Catalogs.TransportConflict",
        _ => "Catalogs.TransportIntegrity"
    }];
}
