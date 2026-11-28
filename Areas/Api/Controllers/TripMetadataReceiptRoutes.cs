using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Shared transport boundary for receipt operations on the two conditional metadata adapters.</summary>
internal sealed class TripMetadataReceiptRoutes(HttpRequest request, ApplicationDbContext db, string subject, bool web,
    Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential)
{
    private TripMetadataOperations Operations => new(db, request.HttpContext.RequestServices.GetRequiredService<TripMetadataProofs>(),
        subject, web, revalidateCredential);

    /// <summary>Uses the existing writer for ordinary edits and preserves exact request bytes on protected edits.</summary>
    internal async Task<(TripMetadataExecution Execution, TripMetadataIdentity? Identity)> PatchAsync(Guid tripId, CancellationToken token)
    {
        var writer = new TripMetadataMutationService(db);
        if (!request.Headers.ContainsKey(TripMetadataIdentity.ProofHeader))
            return (new(await writer.ApplyAsync(tripId, subject, request.Body, web, web ? null : revalidateCredential, token), null), null);
        var proof = ReadProof();
        var permit = await Operations.VerifyAsync(tripId, proof, null, token);
        if (request.Headers.ContainsKey("Content-Encoding"))
            throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "Protected Trip saves require unencoded UTF-8 JSON.");
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, token);
        var execution = await writer.ApplyProtectedAsync(permit, body.ToArray(), revalidateCredential, token);
        return (execution, permit.Identity);
    }

    /// <summary>Creates one permit or returns the ordinary refusal without correlation.</summary>
    internal Task<IActionResult> IssueAsync(Guid tripId, CancellationToken token) =>
        RespondAsync(() => Operations.IssueAsync(tripId, request.Body, token));

    /// <summary>Recovers one authorized operation independently of the Trip graph or target existence.</summary>
    internal Task<IActionResult> LookupAsync(Guid tripId, Guid operationId, CancellationToken token) =>
        RespondAsync(() => Operations.LookupAsync(tripId, operationId, ReadProof(), token));

    /// <summary>Shapes known transport errors without giving bare failures settlement authority.</summary>
    private static async Task<IActionResult> RespondAsync(Func<Task<Dictionary<string, object?>>> work)
    {
        try { return new OkObjectResult(await work()); }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>Returns the same evidence grammar for original execution, exact replay and lookup.</summary>
    internal static IActionResult EvidenceResult(TripMetadataEvidence evidence, TripMetadataIdentity identity) =>
        new ObjectResult(evidence.Envelope(identity)) { StatusCode = evidence.State == "terminalUnapplied" ? 409 : 200 };

    /// <summary>Refuses missing, repeated or oversized proof headers without processing them as permits.</summary>
    private string ReadProof()
    {
        var proofs = request.Headers[TripMetadataIdentity.ProofHeader];
        if (proofs.Count != 1 || proofs[0] is not { Length: > 0 and <= 4096 } proof)
            throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "A single Trip save proof is required.");
        return proof;
    }
}
