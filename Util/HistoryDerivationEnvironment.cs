using System.Globalization;
using System.Reflection;
using System.Text;
using GeoTimeZone;
using NodaTime;

namespace Wayfarer.Util;

/// <summary>
/// Code-owned admission for the reviewed history profile, independent of persisted semantic IDs.
/// A new profile needs a release compatibility decision; rebuilding cannot qualify an environment.
/// </summary>
internal sealed record HistoryDerivationEnvironment(string GeoTimeZone, string NodaTime, string Tzdb,
    string Parser, Version Runtime, int SortVersion, Guid SortId, bool IcuNormalization)
{
    private static readonly Lazy<HistoryDerivationEnvironment> Serving = new(Capture);

    /// <summary>Capture once on first history use, never as an application startup prerequisite.</summary>
    internal static HistoryDerivationEnvironment Current => Serving.Value;

    /// <summary>Exact reviewed .NET servicing profiles; NuGet package versions do not select the executing runtime.</summary>
    private bool RuntimeQualified => Runtime == new Version(10, 0, 13) || Runtime == new Version(10, 0, 14);

    /// <summary>Coordinate mapping and historical rules are independent of ICU text admission.</summary>
    internal bool TimeQualified => RuntimeQualified && GeoTimeZone == "6.1.0+73206b43819c89f8b6ec2926b2ca227fc783b938"
        && NodaTime == "3.3.6+3ad5876deb0edfc3c87622a3717801c616ff570a" && Tzdb == "TZDB: 2026e (mapping: 49)";

    /// <summary>
    /// The reviewed invariant ICU profile is portable across hosts, paths and architectures.
    /// Sort identity is an admission fingerprint, not proof of semantic equivalence; release review and fixtures own that decision.
    /// </summary>
    internal bool TextQualified => RuntimeQualified && Parser == "1.7.2+3718d6804dd3a3781efcd1ccd7bdcb10a4352edd"
        && SortVersion == 31129 && SortId == new Guid("00007999-0000-0000-0000-00000000007f") && IcuNormalization;

    /// <summary>Fail before any selected facts, canonical writes or preparation state can be published.</summary>
    internal void Require(bool time, bool text)
    {
        if (time && !TimeQualified || text && !TextQualified) throw new HistoryQualificationException(this);
    }

    /// <summary>Safe actual provenance for operators; these values never enter cursor or stored generation identity.</summary>
    internal object Diagnostics => new { GeoTimeZone, NodaTime, Tzdb, Parser, Runtime = Runtime.ToString(), SortVersion, SortId,
        IcuNormalization, TimeQualified, TextQualified };

    /// <summary>Inspect loaded artifacts and effective ICU behavior rather than configuration intent or host paths.</summary>
    private static HistoryDerivationEnvironment Capture()
    {
        var sort = CultureInfo.InvariantCulture.CompareInfo.Version;
        var bytes = sort.SortId.ToByteArray();
        var icuVersion = bytes[3] << 24 | bytes[2] << 16 | bytes[1] << 8 | bytes[0];
        var icu = icuVersion != 0 && icuVersion == sort.FullVersion
            && "éＡﬃ".Normalize(NormalizationForm.FormKD) == "e\u0301Affi";
        return new(Artifact(typeof(TimeZoneLookup).Assembly), Artifact(typeof(Instant).Assembly), DateTimeZoneProviders.Tzdb.VersionId,
            Artifact(typeof(AngleSharp.Html.Parser.HtmlParser).Assembly), Environment.Version, sort.FullVersion, sort.SortId, icu);
    }

    /// <summary>Retain the actual package build identity, including the upstream source revision.</summary>
    private static string Artifact(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}

/// <summary>Operational admission failure, never classified as an unresolved Location or repaired by rebuilding.</summary>
internal sealed class HistoryQualificationException(HistoryDerivationEnvironment environment) : InvalidOperationException(
    "History environment qualification is missing. Use a qualified build/runtime/globalization profile; rebuilding does not qualify it.")
{
    internal HistoryDerivationEnvironment Environment { get; } = environment;
}
