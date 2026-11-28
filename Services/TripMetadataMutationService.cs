using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Services;

/// <summary>One PostgreSQL compare/apply boundary for protected bearer and cookie Trip metadata edits.</summary>
internal sealed class TripMetadataMutationService(ApplicationDbContext db)
{
    /// <summary>Preserves the existing unmarked single-transaction writer and response.</summary>
    internal async Task<TripMetadataMutationResult> ApplyAsync(Guid tripId, string userId, Stream body, bool web,
        Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential, CancellationToken token)
    {
        if (!await db.Trips.AsNoTracking().AnyAsync(t => t.Id == tripId && t.UserId == userId, token))
            throw MissingTrip();
        var request = await TripMetadataConditionalRequest.ReadAsync(body, web, token);
        var execution = await CommitAsync(tripId, userId, () => Task.FromResult(request), revalidateCredential, null, token);
        return execution.Mutation ?? throw new InvalidOperationException("Trip metadata commit did not return a mutation.");
    }

    /// <summary>Uses the same compare/apply path, with an operation fence outside the existing Trip/account locks.</summary>
    internal Task<TripMetadataExecution> ApplyProtectedAsync(TripMetadataPermit permit, byte[] bytes,
        Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential, CancellationToken token)
    {
        var identity = permit.Identity;
        if (TripMetadataIdentity.Digest(identity.SubjectId, identity.Adapter, identity.TripId, bytes) != identity.RequestDigest)
            throw new TripMetadataMutationRejected(409, "trip-attempt-digest-mismatch", "The Trip save bytes do not match the issued proof.");
        return CommitAsync(identity.TripId, identity.SubjectId,
            () => ReadProtectedAsync(bytes, identity.Adapter == "web", token), revalidateCredential, permit, token);
    }

    /// <summary>Parses the verified immutable payload with the marked grammar only.</summary>
    private static async Task<TripMetadataConditionalRequest> ReadProtectedAsync(byte[] bytes, bool web, CancellationToken token)
    {
        if (bytes.AsSpan().StartsWith<byte>([0xEF, 0xBB, 0xBF]))
            throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "Protected Trip saves cannot include a UTF-8 BOM.");
        using var body = new MemoryStream(bytes, writable: false);
        return await TripMetadataConditionalRequest.ReadAsync(body, web, token, receipt: true);
    }

    /// <summary>Commits domain changes and terminal evidence together, preserving uncertainty on failed COMMIT.</summary>
    private async Task<TripMetadataExecution> CommitAsync(Guid tripId, string userId,
        Func<Task<TripMetadataConditionalRequest>> readRequest, Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential,
        TripMetadataPermit? permit, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var commitStarted = false;
        try
        {
            var store = new TripMetadataReceiptStore(db);
            if (permit != null)
            {
                var prior = await ReadPriorAsync(store, permit, revalidateCredential, token);
                if (prior != null) return new(null, prior);
            }
            var request = await readRequest();
            // Read the post-wait row independently of any earlier tracked/preflight state.
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token) ?? throw MissingTrip();
            await TripMetadataOperations.RequireActiveAsync(db, userId, revalidateCredential, token);
            if (permit != null) permit.RequireOpen(await store.AdmissionAsync(token));

            var current = Values(trip);
            var stale = request.Expected.Any(field => !Equals(field.Value, current[field.Key]));
            if (stale && permit == null)
                throw new TripMetadataMutationRejected(409, "mutation-base-stale",
                    "The edited Trip fields changed. Keep your draft, review current values and explicitly choose a new base before saving.");
            var mutation = stale ? null : await ApplyFieldsAsync(trip, request, current, token);
            var evidence = permit == null ? null : await RecordAsync(store, permit, mutation, request, token);

            commitStarted = true;
            await transaction.CommitAsync(token);
            return new(mutation, evidence);
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackFailure) { throw new AggregateException("Trip metadata failed and rollback also failed.", original, rollbackFailure); }
            if (!commitStarted && original is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Trip editing contention prevented this attempt. Review current values before saving.");
            throw;
        }
    }

    /// <summary>Historical evidence is independent of current target ownership and is returned before new-work validation.</summary>
    private async Task<TripMetadataEvidence?> ReadPriorAsync(TripMetadataReceiptStore store, TripMetadataPermit permit,
        Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential, CancellationToken token)
    {
        var identity = permit.Identity;
        await store.FenceAsync(identity.SubjectId, identity.OperationId, true, token);
        var prior = await store.ReadAsync(identity, permit.Authority, token);
        if (prior == null) return null;
        await TripMetadataOperations.RequireActiveAsync(db, identity.SubjectId, revalidateCredential, token);
        permit.RequireCurrent(await store.AdmissionAsync(token));
        return prior;
    }

    /// <summary>Captures authoritative comparison fingerprints and the minimum history retention boundary.</summary>
    private static async Task<TripMetadataEvidence> RecordAsync(TripMetadataReceiptStore store, TripMetadataPermit permit,
        TripMetadataMutationResult? mutation, TripMetadataConditionalRequest request, CancellationToken token)
    {
        var completion = await store.AdmissionAsync(token);
        permit.RequireCurrent(completion);
        var deadline = permit.Identity.ExecuteBeforeUtc;
        var retention = (completion.Now > deadline ? completion.Now : deadline).AddDays(30);
        var state = mutation == null ? "terminalUnapplied" : mutation.Applied ? "applied" : "checkedNoOp";
        var evidence = new TripMetadataEvidence(state, mutation == null ? "unapplied" : state, false)
        {
            CompletedAtUtc = completion.Now, ReceiptRetainUntilUtc = retention,
            Code = mutation == null ? "mutation-base-stale" : null, UpdatedAtUtc = mutation?.Trip.UpdatedAt,
            FieldDigests = mutation == null ? null : request.Changes.Keys.ToDictionary(field => field,
                field => TripMetadataIdentity.FieldDigest(field, field == "name" ? mutation.Trip.Name : RichNotes.Normalize(mutation.Trip.Notes)))
        };
        await store.RecordAsync(permit.Identity, permit.Authority, evidence, token);
        return evidence;
    }

    /// <summary>Applies only the selected metadata and existing derived privacy effects.</summary>
    private async Task<TripMetadataMutationResult> ApplyFieldsAsync(Trip trip, TripMetadataConditionalRequest request,
        Dictionary<string, object?> current, CancellationToken token)
    {
        if (request.Changes.TryGetValue("isPublic", out var publish) && publish is true
            && request.Changes.GetValueOrDefault("contributionPolicy", trip.ContributionPolicy) == null)
            throw new TripMetadataMutationRejected(400, "publication-policy-required", "Choose a contribution policy before publication.");
        var beforeImages = Images(trip);
        var disableSharing = request.Changes.TryGetValue("isPublic", out var publication) && publication is false && trip.ShareProgressEnabled;
        var applied = request.Changes.Any(field => !Equals(field.Value, field.Key == "notes" ? trip.Notes : current[field.Key])) || disableSharing;
        if (applied)
        {
            var now = DateTime.UtcNow;
            // PostgreSQL timestamps persist microseconds.
            var stamp = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
            trip.UpdatedAt = trip.UpdatedAt > stamp ? trip.UpdatedAt : stamp;
            await WriteAsync(trip, request, disableSharing, token);
        }
        var afterImages = Images(trip);
        return new(trip, applied, disableSharing, request.Changes.ContainsKey("notes"),
            afterImages.Any(url => !beforeImages.Contains(url)), beforeImages.Count == 0 && afterImages.Count > 0);
    }

    /// <summary>Reports unavailable target scope without disclosing private Trip state.</summary>
    internal static TripMetadataMutationRejected MissingTrip() =>
        new(404, "mutation-scope-unavailable", "Trip not found.");

    /// <summary>Uses dynamic property setters without attaching a stale Trip or saving unrelated tracked work.</summary>
    private async Task WriteAsync(Trip trip, TripMetadataConditionalRequest request, bool disableSharing, CancellationToken token)
    {
        await db.Trips.Where(t => t.Id == trip.Id && t.UserId == trip.UserId).ExecuteUpdateAsync(setters =>
        {
            foreach (var (key, value) in request.Changes)
            {
                switch (key)
                {
                    case "contributionPolicy":
                        trip.ContributionPolicy = (string?)value;
                        setters.SetProperty(t => t.ContributionPolicy, trip.ContributionPolicy);
                        break;
                    case "credits":
                        trip.Credits = TripCreditCodec.Read((string)value!);
                        setters.SetProperty(t => t.Credits, trip.Credits);
                        break;
                    case "name":
                        trip.Name = (string)value!;
                        setters.SetProperty(t => t.Name, trip.Name);
                        break;
                    case "notes":
                        trip.Notes = (string?)value;
                        setters.SetProperty(t => t.Notes, trip.Notes);
                        break;
                    case "isPublic":
                        trip.IsPublic = (bool)value!;
                        setters.SetProperty(t => t.IsPublic, trip.IsPublic);
                        break;
                    case "coverImage":
                        trip.CoverImageUrl = (string?)value;
                        setters.SetProperty(t => t.CoverImageUrl, trip.CoverImageUrl);
                        break;
                    case "center":
                        var center = (TripMetadataCenter)value!;
                        trip.CenterLat = center.Latitude;
                        trip.CenterLon = center.Longitude;
                        setters.SetProperty(t => t.CenterLat, trip.CenterLat).SetProperty(t => t.CenterLon, trip.CenterLon);
                        break;
                    case "zoom":
                        trip.Zoom = (int?)value;
                        setters.SetProperty(t => t.Zoom, trip.Zoom);
                        break;
                }
            }
            if (disableSharing)
            {
                trip.ShareProgressEnabled = false;
                setters.SetProperty(t => t.ShareProgressEnabled, false);
            }
            setters.SetProperty(t => t.UpdatedAt, trip.UpdatedAt);
        }, token);
    }

    /// <summary>Published original Notes are normalized without conflating null and empty.</summary>
    private static Dictionary<string, object?> Values(Trip trip) => new()
    {
        ["contributionPolicy"] = trip.ContributionPolicy, ["credits"] = TripCreditCodec.Write(trip.Credits),
        ["name"] = trip.Name, ["notes"] = RichNotes.Normalize(trip.Notes), ["isPublic"] = trip.IsPublic,
        ["coverImage"] = trip.CoverImageUrl, ["center"] = new TripMetadataCenter(trip.CenterLat, trip.CenterLon), ["zoom"] = trip.Zoom
    };

    /// <summary>Compares external image identity for the existing post-commit warm-up gates.</summary>
    private static HashSet<string> Images(Trip trip) => HtmlHelpers.ExtractExternalImageUrls(trip.Notes)
        .Concat(string.IsNullOrWhiteSpace(trip.CoverImageUrl) ? [] : new[] { trip.CoverImageUrl.Trim() })
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A new commit may schedule existing external work; terminal replay supplies evidence only.</summary>
internal sealed record TripMetadataExecution(TripMetadataMutationResult? Mutation, TripMetadataEvidence? Evidence);

/// <summary>Confirmed result from one new compare/apply execution.</summary>
internal sealed record TripMetadataMutationResult(Trip Trip, bool Applied, bool DisabledShareProgress, bool NotesRequested,
    bool ImagesIntroduced, bool FirstImagesIntroduced);

/// <summary>Nonleaking known-unapplied errors; unknown commit and transport failures never use this type.</summary>
internal sealed class TripMetadataMutationRejected(int status, string code, string detail, Dictionary<string, string[]>? errors = null) : Exception(detail)
{
    /// <summary>Projects the approved ProblemDetails contract without current private values or credentials.</summary>
    internal IActionResult ToActionResult()
    {
        ProblemDetails problem = errors == null ? new ProblemDetails() : new ValidationProblemDetails(errors);
        problem.Status = status;
        problem.Title = status == 409 ? "Trip edit needs review." : "Trip edit was not applied.";
        problem.Detail = Message;
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
