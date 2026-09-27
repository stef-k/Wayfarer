namespace Wayfarer.Util;

/// <summary>Owns the fixed application ceiling and the two user uploads' configurable request policy.</summary>
public static class UploadRequestPolicy
{
    /// <summary>Compatibility ceiling for every application request, in binary mebibytes.</summary>
    public const int MaximumRequestMiB = 100;

    /// <summary>The fixed application request ceiling in bytes.</summary>
    public const long MaximumRequestBytes = MaximumRequestMiB * 1024L * 1024L;

    /// <summary>Normalizes historical/unset values; invalid negative values disable uploads.</summary>
    public static int EffectiveMiB(int configured) => configured switch
    {
        < 0 => -1,
        0 => MaximumRequestMiB,
        _ => Math.Min(configured, MaximumRequestMiB)
    };

    /// <summary>Returns a checked byte ceiling, or zero when uploads are disabled.</summary>
    public static long EffectiveBytes(int configured) => checked(Math.Max(0, EffectiveMiB(configured)) * 1024L * 1024L);
}
