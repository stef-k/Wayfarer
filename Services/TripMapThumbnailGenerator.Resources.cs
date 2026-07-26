using System.Runtime.InteropServices;
using Microsoft.Playwright;

namespace Wayfarer.Services;

/// <summary>
/// Owns temporary-file publication and browser-resource cleanup for thumbnail generation.
/// </summary>
public sealed partial class TripMapThumbnailGenerator
{
    private static Action<string, string> _replaceThumbnailFile = ReplaceThumbnailFileAtomicallyCore;

    /// <summary>Writes and timestamps a complete same-directory file before atomically publishing it.</summary>
    private static async Task PersistThumbnailAsync(
        string filePath,
        byte[] thumbnailBytes,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath) ?? ".";
        var tempFilePath = Path.Combine(
            directory,
            $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(tempFilePath, thumbnailBytes, cancellationToken);
            File.SetLastWriteTimeUtc(tempFilePath, updatedAt);
            _replaceThumbnailFile(tempFilePath, filePath);
        }
        finally
        {
            TryDeleteTemporaryThumbnail(tempFilePath);
        }
    }

    /// <summary>Atomically replaces an existing thumbnail or publishes the first thumbnail.</summary>
    private static void ReplaceThumbnailFileAtomicallyCore(string tempFilePath, string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Replace(tempFilePath, filePath, null);
            return;
        }

        File.Move(tempFilePath, filePath);
    }

    /// <summary>Removes an unpublished temporary thumbnail without masking the original failure.</summary>
    private static void TryDeleteTemporaryThumbnail(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch
        {
            // Cleanup is best-effort so the original persistence failure remains authoritative.
        }
    }

    /// <summary>Overrides thumbnail replacement for deterministic atomic-persistence tests.</summary>
    internal static void SetThumbnailFileReplacerForTesting(Action<string, string>? replacer)
    {
        _replaceThumbnailFile = replacer ?? ReplaceThumbnailFileAtomicallyCore;
    }

    /// <summary>Builds the Chromium launch arguments required for loopback thumbnail capture.</summary>
    private static List<string> CreateLaunchArguments(string hostResolverRule)
    {
        var launchArgs = new List<string>
        {
            "--ignore-certificate-errors",
            "--disable-web-security",
            $"--host-resolver-rules={hostResolverRule}"
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            launchArgs.AddRange(new[]
            {
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu"
            });
        }

        return launchArgs;
    }

    /// <summary>Closes every created capture resource once and returns the first cleanup failure.</summary>
    private static async Task<Exception?> DisposeCaptureResourcesAsync(
        IPage? page,
        IBrowser? browser,
        IPlaywright? playwright)
    {
        Exception? firstException = null;

        try
        {
            if (page != null)
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            firstException = ex;
        }

        try
        {
            if (browser != null)
            {
                await browser.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        try
        {
            playwright?.Dispose();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        return firstException;
    }
}
