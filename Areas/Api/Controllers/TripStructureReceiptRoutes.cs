using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Serves the literal protected paths while leaving their unmarked bearer routes independent.</summary>
internal sealed class TripStructureReceiptRoutes(HttpRequest request, ApplicationDbContext db, string subject, bool web,
    Func<ApplicationUser, CancellationToken, Task<bool>> credential)
{
    /// <summary>Routes issue, observation and execution through one adapter-bound operation identity.</summary>
    internal async Task<IActionResult> DispatchAsync(Guid tripId, TripReceiptTarget target, string kind, string writeMethod,
        Guid? operationId, Func<TripStructureWrite, Task<TripStructureResult>> apply, Func<Task>? afterWrite, CancellationToken token, bool serializable = false,
        Func<TripStructureWrite, Func<CancellationToken, Task>, Task<Func<Task<TripPlaceProviderResult>>>>? prepareProvider = null)
    {
        request.HttpContext.Response.Headers.CacheControl = "private, no-store";
        try
        {
            if (tripId == Guid.Empty || target.Id == Guid.Empty || operationId == Guid.Empty) throw TripStructureBodies.Invalid();
            var proofs = request.HttpContext.RequestServices.GetRequiredService<TripMetadataProofs>();
            var operations = new TripMetadataOperations(db, proofs, subject, web, credential, kind, target);
            if (operationId is { } id)
            {
                if (request.QueryString.HasValue || request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
                    throw TripStructureBodies.Invalid();
                using var stream = new MemoryStream();
                await request.Body.CopyToAsync(stream, token);
                if (stream.Length != 0) throw TripStructureBodies.Invalid();
                return new OkObjectResult(await operations.LookupAsync(tripId, id, ReadProof(), token));
            }

            var body = await ReadBodyAsync(token);
            if (writeMethod != request.Method || request.Path.Value!.TrimEnd('/').EndsWith("/operations", StringComparison.OrdinalIgnoreCase))
            {
                _ = TripStructureBodies.ReadObject(body);
                using var input = new MemoryStream(body);
                return new OkObjectResult(await operations.IssueAsync(tripId, input, token,
                    requireEligibility: ct => TripStructureEligibility.RequireAsync(db, subject, tripId, target, kind, web, ct)));
            }

            var permit = await operations.VerifyAsync(tripId, ReadProof(), null, token);
            var independent = serializable ? request.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<ApplicationDbContext>>() : null;
            var (evidence, newWrite) = await new TripStructureExecution(db, permit, credential, independent).ExecuteAsync(body,
                (payload, readmit, frozen) => apply(new(subject, web, tripId, target.Id, payload, readmit, token, frozen)), token,
                prepareProvider == null ? null : (payload, check) => prepareProvider(
                    new(subject, web, tripId, target.Id, payload, _ => throw new InvalidOperationException("Provider preflight cannot admit a write."), token), check));
            if (newWrite && afterWrite != null) await afterWrite();
            return new ObjectResult(evidence.Envelope(permit.Identity))
            {
                StatusCode = evidence.State switch
                {
                    "terminalUnapplied" => TripStructureExecution.TerminalStatus(evidence.Code),
                    "unresolved" => 503,
                    "unobserved" or "retired" => 409,
                    _ => 200
                }
            };
        }
        catch (TripMetadataMutationRejected error) { return error.ToActionResult(); }
    }

    /// <summary>Reads one strict qualified preview without issuing a permit or applying effects.</summary>
    internal async Task<IActionResult> PreviewAsync(Guid tripId, Guid targetId,
        Func<TripStructureWrite, Task<object>> preview, CancellationToken token)
    {
        request.HttpContext.Response.Headers.CacheControl = "private, no-store";
        try
        {
            if (tripId == Guid.Empty || targetId == Guid.Empty) throw TripStructureBodies.Invalid();
            var body = TripStructureBodies.ReadObject(await ReadBodyAsync(token));
            return new OkObjectResult(await preview(new(subject, web, tripId, targetId, body,
                _ => throw new InvalidOperationException("Preview cannot admit a write."), token)));
        }
        catch (TripMetadataMutationRejected error) { return error.ToActionResult(); }
    }

    /// <summary>Requires an unencoded JSON request with no effect-bearing query alternatives.</summary>
    private async Task<byte[]> ReadBodyAsync(CancellationToken token)
    {
        if (request.QueryString.HasValue || request.Headers.ContainsKey("Content-Encoding")
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var content)
            || !string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || content.Parameters.Count > 1 || content.Parameters.Any(parameter =>
                !parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parameter.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw TripStructureBodies.Invalid();

        using var stream = new MemoryStream();
        await request.Body.CopyToAsync(stream, token);
        return stream.ToArray();
    }

    /// <summary>Accepts exactly one bounded proof header.</summary>
    private string ReadProof()
    {
        var values = request.Headers[TripMetadataIdentity.ProofHeader];
        return values.Count == 1 && values[0] is { Length: > 0 and <= 4096 } proof ? proof : throw TripStructureBodies.Invalid();
    }
}
