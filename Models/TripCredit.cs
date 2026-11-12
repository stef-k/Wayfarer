namespace Wayfarer.Models;

/// <summary>Publishable, self-contained inherited attribution, independent of source accounts and local identities.</summary>
public sealed record TripCredit(string Contributor, string SourceTitle, string Licence,
    string? LicenceUrl = null, string? Notices = null, string? Modifications = null, string? SourceUrl = null);
