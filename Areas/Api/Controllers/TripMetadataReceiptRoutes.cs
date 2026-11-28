using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Shared transport boundary for receipt operations on the two conditional metadata adapters.</summary>
internal sealed class TripMetadataReceiptRoutes(HttpRequest request, ApplicationDbContext db, string subject, bool web,
    Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential, string kind = TripMetadataIdentity.OperationKind)
{
    private TripMetadataOperations Operations => new(db, request.HttpContext.RequestServices.GetRequiredService<TripMetadataProofs>(),
        subject, web, revalidateCredential, kind);

    /// <summary>Uses the existing writer for ordinary edits and preserves exact request bytes on protected edits.</summary>
    internal async Task<(TripMetadataExecution Execution, TripMetadataIdentity? Identity)> PatchAsync(Guid tripId, CancellationToken token)
    {
        var writer = new TripMetadataMutationService(db);
        if (!request.Headers.ContainsKey(TripMetadataIdentity.ProofHeader) && kind == TripMetadataIdentity.OperationKind)
        {
            if (web) throw await RefuseUnprovedWebWriteAsync(token);
            return (new(await writer.ApplyAsync(tripId, subject, request.Body, false, revalidateCredential, token), null), null);
        }
        var proof = ReadProof();
        var permit = await Operations.VerifyAsync(tripId, proof, null, token);
        ValidateTransport(permit.Identity.Contract == TripMetadataIdentity.Version2);
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, token);
        var execution = await writer.ApplyProtectedAsync(permit, body.ToArray(), revalidateCredential, token);
        return (execution, permit.Identity);
    }

    /// <summary>Creates one permit or returns the ordinary refusal without correlation.</summary>
    internal Task<IActionResult> IssueAsync(Guid tripId, CancellationToken token) =>
        RespondAsync(() =>
        {
            ValidateTransport(strict: true);
            return Operations.IssueAsync(tripId, request.Body, token);
        });

    /// <summary>Recovers one authorized operation independently of the Trip graph or target existence.</summary>
    internal Task<IActionResult> LookupAsync(Guid tripId, Guid operationId, CancellationToken token) =>
        RespondAsync(() => Operations.LookupAsync(tripId, operationId, ReadProof(), token));

    /// <summary>Runs an operation whose only successful transport is correlated evidence.</summary>
    internal async Task<IActionResult> ExecuteAsync(Guid tripId, CancellationToken token)
    {
        try
        {
            var (execution, identity) = await PatchAsync(tripId, token);
            return EvidenceResult(execution.Evidence ?? throw new InvalidOperationException("Missing Trip control evidence."),
                identity ?? throw new InvalidOperationException("Missing Trip control identity."));
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>Binds the current User-cookie stamp to the final locked account check.</summary>
    internal static Task<IActionResult> WithWebAuthorityAsync(HttpContext context, ApplicationDbContext db, string kind,
        Func<TripMetadataReceiptRoutes, Task<IActionResult>> dispatch)
    {
        var account = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(account) || context.User.Identity?.IsAuthenticated != true)
            return Task.FromResult<IActionResult>(new UnauthorizedResult());
        if (!context.User.IsInRole("User")) return Task.FromResult<IActionResult>(new ForbidResult());
        var claim = context.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var stamp = context.User.FindFirst(claim)?.Value;
        return dispatch(new(context.Request, db, account, true, (user, _) =>
            Task.FromResult(!string.IsNullOrEmpty(stamp) && string.Equals(stamp, user.SecurityStamp, StringComparison.Ordinal)), kind));
    }

    /// <summary>Distinguishes a stale Web invocation from a marked request missing its proof.</summary>
    private async Task<TripMetadataMutationRejected> RefuseUnprovedWebWriteAsync(CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("receiptContract", out _))
                return new(400, "mutation-request-invalid", "A single Trip save proof is required.");
        }
        catch (JsonException) { }
        return UpgradeRequired();
    }

    /// <summary>Refuses obsolete cookie writes even from pages loaded before cutover.</summary>
    internal static TripMetadataMutationRejected UpgradeRequired() =>
        new(409, "trip-write-upgrade-required", "Reload this page to use protected Trip settings saves.");

    /// <summary>Rejects alternate effect inputs and encoded protected request bodies.</summary>
    private void ValidateTransport(bool strict)
    {
        if (request.Headers.ContainsKey("Content-Encoding") || strict && (request.QueryString.HasValue || !request.HasJsonContentType()))
            throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "Protected Trip saves require unencoded UTF-8 JSON.");
    }

    /// <summary>Shapes known transport errors without giving bare failures settlement authority.</summary>
    private static async Task<IActionResult> RespondAsync(Func<Task<Dictionary<string, object?>>> work)
    {
        try { return new OkObjectResult(await work()); }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>Returns the same evidence grammar for original execution, exact replay and lookup.</summary>
    internal static IActionResult EvidenceResult(TripMetadataEvidence evidence, TripMetadataIdentity identity) =>
        new ObjectResult(evidence.Envelope(identity))
        {
            StatusCode = evidence.State != "terminalUnapplied" ? 200 : evidence.Code switch
            {
                "publication-policy-required" or "share-progress-requires-public" => 400,
                "tag-not-on-trip" => 404,
                _ => 409
            }
        };

    /// <summary>Refuses missing, repeated or oversized proof headers without processing them as permits.</summary>
    private string ReadProof()
    {
        var proofs = request.Headers[TripMetadataIdentity.ProofHeader];
        if (proofs.Count != 1 || proofs[0] is not { Length: > 0 and <= 4096 } proof)
            throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "A single Trip save proof is required.");
        return proof;
    }
}
