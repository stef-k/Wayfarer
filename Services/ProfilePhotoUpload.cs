using System.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Wayfarer.Parsers;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>One no-queue avatar slot, admitted before multipart reads, with one fixed overall lifetime.</summary>
public sealed class ProfilePhotoUpload(ProfilePhotoService photos, IApplicationSettingsService settings, TimeProvider time)
{
    private static readonly SemaphoreSlim Slot = new(1, 1);
    /// <summary>Existing helper boundary, replaceable only in focused cancellation/budget tests.</summary>
    internal Func<ReadOnlyMemory<byte>, ProfilePhotoCrop?, TimeSpan, CancellationToken, Task<ImageTransformResult>> TransformAsync { get; init; }
        = ImageProxyHelper.Supervisor.AvatarAsync;
    internal const int MaximumFileBytes = 15 * 1024 * 1024;

    /// <summary>Body receipt, helper and commit share 60 seconds; termination/reap has an independent bounded watchdog.</summary>
    public async Task<ProfilePhotoState> UploadAsync(HttpRequest request, string owner, CancellationToken requestAbort)
    {
        if (!Slot.Wait(0)) throw new ProfilePhotoException(503, "photo_processing_unavailable");
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAbort, overall.Token);
        var started = time.GetTimestamp();
        try
        {
            var ct = linked.Token;
            var maximum = Math.Min(MaximumFileBytes, UploadRequestPolicy.EffectiveBytes(settings.GetSettings().UploadSizeLimitMB));
            if (maximum == 0) throw new ProfilePhotoException(403, "photo_upload_disabled");
            var (bytes, token, crop) = await ReadAsync(request, (int)maximum, ct);
            var remaining = TimeSpan.FromSeconds(60) - time.GetElapsedTime(started);
            ct.ThrowIfCancellationRequested();
            var result = await TransformAsync(bytes, crop, remaining, ct);
            if (result.Status != ImageTransformStatus.Success)
                throw result.Status switch
                {
                    ImageTransformStatus.TooLarge => new ProfilePhotoException(413, "photo_too_large"),
                    ImageTransformStatus.Unsupported => new ProfilePhotoException(415, "unsupported_photo_format"),
                    ImageTransformStatus.Failed => new ProfilePhotoException(400, "invalid_photo"),
                    _ when result.ExitCode == 2 => new ProfilePhotoException(400, "invalid_crop"),
                    _ => new ProfilePhotoException(503, "photo_processing_unavailable")
                };
            return await photos.MutateAsync(owner, token, result.Bytes!, ct);
        }
        catch (OperationCanceledException) when (overall.IsCancellationRequested && !requestAbort.IsCancellationRequested)
        {
            // MultipartReader never drains the outstanding body. Kestrel owns connection
            // termination after this response; no processing/publication starts after expiry.
            throw new ProfilePhotoException(408, "photo_upload_timeout");
        }
        finally { Slot.Release(); }
    }

    /// <summary>One bounded file and four small fields; no model binder or raw-original persistence.</summary>
    private static async Task<(byte[] Bytes, string Token, ProfilePhotoCrop? Crop)> ReadAsync(HttpRequest request, int maximum, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ProfilePhotoException(400, "invalid_photo");
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new ProfilePhotoException(400, "invalid_photo");
        var reader = new MultipartReader(boundary, request.Body)
        { HeadersCountLimit = 8, HeadersLengthLimit = 4096, BodyLengthLimit = 16 * 1024 * 1024 };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? bytes = null;
        MultipartSection? section;
        var sections = 0;
        try
        {
            while ((section = await reader.ReadNextSectionAsync(ct)) != null)
            {
                if (++sections > 5 || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || disposition.DispositionType != "form-data") throw new ProfilePhotoException(400, "invalid_photo");
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                if (disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
                {
                    if (bytes != null || name != "photo") throw new ProfilePhotoException(400, "invalid_photo");
                    bytes = await ReadPartAsync(section.Body, maximum, ct);
                }
                else
                {
                    if (name is not ("expectedStateToken" or "cropX" or "cropY" or "cropSize") || fields.ContainsKey(name))
                        throw new ProfilePhotoException(400, "invalid_photo");
                    fields.Add(name, System.Text.Encoding.UTF8.GetString(await ReadPartAsync(section.Body, 1024, ct)));
                }
            }
        }
        catch (InvalidDataException) { throw new ProfilePhotoException(400, "invalid_photo"); }
        if (bytes is not { Length: > 0 }) throw new ProfilePhotoException(400, "invalid_photo");
        if (!fields.TryGetValue("expectedStateToken", out var token) || string.IsNullOrWhiteSpace(token))
            throw new ProfilePhotoException(400, "invalid_photo_state");
        var cropFields = new[] { "cropX", "cropY", "cropSize" }.Count(fields.ContainsKey);
        ProfilePhotoCrop? crop = null;
        if (cropFields != 0)
        {
            if (cropFields != 3 || !Integer(fields["cropX"], out var x) || !Integer(fields["cropY"], out var y)
                || !Integer(fields["cropSize"], out var size) || size == 0) throw new ProfilePhotoException(400, "invalid_crop");
            crop = new(x, y, size);
        }
        return (bytes, token, crop);
    }

    /// <summary>Count actual bytes before adding them; cancellation reaches the actual client stream.</summary>
    private static async Task<byte[]> ReadPartAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (read > maximum - output.Length) throw new ProfilePhotoException(413, "photo_too_large");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>No floating-point, signs, exponents or browser geometry assertions.</summary>
    private static bool Integer(string value, out int result) => int.TryParse(value,
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out result);
}
