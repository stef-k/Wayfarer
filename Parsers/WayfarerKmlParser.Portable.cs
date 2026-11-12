using System.Xml.Linq;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

public static partial class WayfarerKmlParser
{
    /// <summary>Reserves all portable claims throughout XML before any legacy/generic fallback can discard them.</summary>
    private static void ValidatePortablePlacement(XDocument source, XElement document, IReadOnlyList<string> versions)
    {
        string[] reserved = ["Credits", "ContentReuse", "Title", "WayfarerSchemaVersion", "WayfarerAttribution"];
        var claims = source.Descendants().Where(e => reserved.Any(name =>
            e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || ((string?)e.Attribute("name"))?.Trim().Equals(name, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        foreach (var claim in claims)
        {
            var name = (string?)claim.Attribute("name");
            var owner = claim.Parent?.Parent;
            if (claim.Name != Kml + "Data" || name == null || !reserved.Contains(name)
                || claim.Parent?.Name != Kml + "ExtendedData" || owner == null
                || claim.Elements(Kml + "value").Count() != 1 || claim.Element(Kml + "value")!.HasElements
                || claim.Elements().Count() != 1)
                throw new FormatException("Malformed or misplaced portable metadata.");
            if (name == "WayfarerSchemaVersion")
            {
                if (owner != document) throw new FormatException("Misplaced native schema version.");
                continue;
            }
            if (name == "WayfarerAttribution")
            {
                if (owner != document || versions.Count != 0) throw new FormatException("Attribution requires generic Document placement.");
                continue;
            }
            if (versions.Count != 1 || versions[0].Trim() != "5"
                || name == "Title" && NativeOwnerKind(owner, document) != "segment"
                || name != "Title" && owner != document)
                throw new FormatException("Portable fields require native v5 and the correct owner.");
        }
        foreach (var group in claims.GroupBy(e => (e.Parent?.Parent, Name: (string?)e.Attribute("name"))))
            if (group.Count() != 1) throw new FormatException("Duplicate portable metadata.");
        _ = ReadGoogleAttribution(source);
    }

    /// <summary>Generic parser callers receive the same fail-closed admission as the native dispatcher.</summary>
    internal static TripCreditCodec.Attribution? ReadGoogleAttribution(XDocument source)
    {
        var document = source.Root?.Element(Kml + "Document") ?? throw new FormatException("Missing KML Document.");
        var claims = source.Descendants().Where(e => e.Name.LocalName.Equals("WayfarerAttribution", StringComparison.OrdinalIgnoreCase)
            || ((string?)e.Attribute("name"))?.Trim().Equals("WayfarerAttribution", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (claims.Length == 0) return null;
        if (claims.Length != 1) throw new FormatException("Duplicate attribution.");
        var claim = claims[0];
        if (source.Root?.Name != Kml + "kml" || source.Root.Elements(Kml + "Document").Count() != 1
            || claim.Name != Kml + "Data" || (string?)claim.Attribute("name") != "WayfarerAttribution"
            || claim.Parent?.Name != Kml + "ExtendedData" || claim.Parent.Parent != document
            || claim.Elements().Count() != 1 || claim.Elements(Kml + "value").Count() != 1 || claim.Element(Kml + "value")!.HasElements)
            throw new FormatException("Malformed or misplaced attribution.");
        var value = TripCreditCodec.ReadAttribution(claim.Element(Kml + "value")!.Value);
        if (document.Elements(Kml + "description").Count() != 1
            || document.Element(Kml + "description")!.Value != TripCreditCodec.Readable(value))
            throw new FormatException("Readable and typed attribution do not agree.");
        return value;
    }
}
