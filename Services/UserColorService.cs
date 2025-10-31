using System;

namespace Wayfarer.Services;

/// <summary>
/// Deterministic HSL hashing to match existing web color logic.
/// </summary>
public class UserColorService : IUserColorService
{
    private const double Saturation = 0.8;
    private const double Lightness = 0.45;

    public string GetColorHex(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            key = "user";
        }

        var hue = 0;
        foreach (var ch in key)
        {
            hue = (hue * 31 + ch) % 360;
        }

        var (r, g, b) = HslToRgb(hue / 360d, Saturation, Lightness);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static (int r, int g, int b) HslToRgb(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 1d / 3d);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1d / 3d);
        }

        return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1d / 6d) return p + (q - p) * 6 * t;
        if (t < 1d / 2d) return q;
        if (t < 2d / 3d) return p + (q - p) * (2d / 3d - t) * 6;
        return p;
    }
}
