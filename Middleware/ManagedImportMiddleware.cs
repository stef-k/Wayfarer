using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;

namespace Wayfarer.Middleware;

/// <summary>Marks the narrow bearer adapter; Web cookie/antiforgery behavior is untouched.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ManagedImportAttribute : Attribute;

/// <summary>Marks only raw PUT, whose replay must run before dynamic upload policy.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ManagedImportUploadAttribute : Attribute;

/// <summary>Strict active bearer admission and accepted-replay precedence before settings or raw bytes.</summary>
public sealed class ManagedImportMiddleware(RequestDelegate next)
{
    private static readonly ApiWorkAdmission Controls = new(16, 4);
    internal static readonly object RequestKey = new();
    internal sealed record Admission(string UserId, string Credential, Guid? RequestId, ManagedImportFingerprint? Fingerprint);

    public async Task InvokeAsync(HttpContext context, ApplicationDbContext db, ManagedImportStatus status)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ManagedImportAttribute>() is null) { await next(context); return; }
        context.Response.OnStarting(() => { context.Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
        context.Response.Headers.RetryAfter = "5";
        try
        {
            var values = context.Request.Headers.Authorization;
            if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var authorization)
                || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(authorization.Parameter) || authorization.Parameter.Any(char.IsWhiteSpace))
                throw new ManagedImportException(401, "unauthorized");
            using var read = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            read.CancelAfter(TimeSpan.FromSeconds(5));
            var user = await IncomingApiTokenResolver.ForRequest(context, db).ResolveTokenAsync(authorization.Parameter,
                RateLimitHelper.GetClientIpAddress(context), read.Token);
            if (user is not { IsActive: true }) throw new ManagedImportException(401, "unauthorized");
            using var permit = Controls.TryAcquire(user.Id, out var denied);
            if (permit is null) throw new ManagedImportException(denied, "import_capacity");
            Guid? requestId = null;
            ManagedImportFingerprint? fingerprint = null;
            if (context.GetEndpoint()!.Metadata.GetMetadata<ManagedImportUploadAttribute>() is not null)
            {
                (requestId, fingerprint) = Parse(context.Request);
                var row = await status.UploadAsync(user.Id, requestId.Value, read.Token);
                if (row is not null)
                {
                    if (!fingerprint.Matches(row)) throw new ManagedImportException(409, "upload_request_conflict");
                    if (row.AcceptedAtUtc.HasValue || row.FileState != ManagedImportFileState.Removed)
                    {
                        context.Response.StatusCode = ManagedImportStatus.HttpStatus(row);
                        await context.Response.WriteAsJsonAsync(ManagedImportStatus.Project(row), context.RequestAborted);
                        return;
                    }
                }
            }
            context.Items[RequestKey] = new Admission(user.Id, authorization.Parameter, requestId, fingerprint);
            await next(context);
            if (!context.Response.HasStarted && context.Response.StatusCode >= 400)
                await ErrorAsync(context, context.Response.StatusCode, "import_request_rejected");
        }
        catch (ManagedImportException failure) { await ErrorAsync(context, failure.Status, failure.Code); }
        catch (ApiTokenAdmissionException)
        { await ErrorAsync(context, IncomingApiTokenResolver.GetDeniedStatus(context), "import_capacity"); }
        catch (Exception error) when (error is DbUpdateException or Npgsql.NpgsqlException or TimeoutException or OperationCanceledException)
        { if (!context.RequestAborted.IsCancellationRequested) await ErrorAsync(context, 503, "import_outcome_unknown"); }
        catch (IOException error)
        { await ErrorAsync(context, (error.HResult & 0xffff) is 28 or 122 ? 507 : 503,
            (error.HResult & 0xffff) is 28 or 122 ? "insufficient_storage" : "import_storage_unavailable"); }
        catch (UnauthorizedAccessException) { await ErrorAsync(context, 503, "import_storage_unavailable"); }
    }

    /// <summary>Syntax checks intentionally do not compare previously accepted bytes to today's upload policy.</summary>
    private static (Guid, ManagedImportFingerprint) Parse(HttpRequest request)
    {
        if (!Guid.TryParse(request.RouteValues["requestId"]?.ToString(), out var id) || id == Guid.Empty
            || request.Query.Keys.Any(key => key != "format")) throw new ManagedImportException(400, "invalid_upload_metadata");
        var format = request.Query["format"].ToString();
        var length = request.Headers["X-File-Bytes"];
        var digest = request.Headers["X-File-SHA256"];
        var choice = request.Headers["X-Enrichment-Requested"];
        if (format is not "csv" and not "wayfarer-geojson" || length.Count != 1
            || !long.TryParse(length[0], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0
            || digest.Count != 1 || digest[0]?.Length != 64 || digest[0]!.Any(character => !char.IsAsciiHexDigitLower(character))
            || choice.Count != 1 || choice[0] is not "true" and not "false"
            || (request.ContentLength.HasValue && request.ContentLength != bytes))
            throw new ManagedImportException(400, "invalid_upload_metadata");
        return (id, new(format, bytes, digest[0]!, choice[0] == "true"));
    }

    private static async Task ErrorAsync(HttpContext context, int status, string code)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        if (status is 401 or 403 or 404)
        {
            await context.Response.WriteAsJsonAsync(new { status,
                error = status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" : "Not Found",
                message = status == 401 ? "Authentication is required to access this endpoint."
                    : status == 403 ? "You do not have permission to access this resource." : "The requested API endpoint does not exist." });
            return;
        }
        await context.Response.WriteAsJsonAsync(new { code, message = "Location import request could not be completed." });
    }
}
