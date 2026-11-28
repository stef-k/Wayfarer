using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Services;

/// <summary>One PostgreSQL compare/apply boundary for protected bearer and cookie Trip metadata edits.</summary>
internal sealed partial class TripMetadataMutationService(ApplicationDbContext db)
{
    /// <summary>Preserves the ordinary bearer writer and its response.</summary>
    internal async Task<TripMetadataMutationResult> ApplyAsync(Guid tripId, string userId, Stream body, bool web,
        Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential, CancellationToken token)
    {
        if (!await db.Trips.AsNoTracking().AnyAsync(t => t.Id == tripId && t.UserId == userId, token))
            throw MissingTrip();
        var request = await TripMetadataConditionalRequest.ReadAsync(body, web, token);
        var execution = await CommitAsync(tripId, userId, null, revalidateCredential, async (trip, _) =>
            MetadataEffect(await CompareAndApplyAsync(trip, request, token), request, null), token);
        return execution.Mutation ?? throw new InvalidOperationException("Trip metadata commit did not return a mutation.");
    }

    /// <summary>Authenticates the frozen bytes before acquiring any domain lock.</summary>
    internal Task<TripMetadataExecution> ApplyProtectedAsync(TripMetadataPermit permit, byte[] bytes,
        Func<ApplicationUser, CancellationToken, Task<bool>>? revalidateCredential, CancellationToken token)
    {
        var identity = permit.Identity;
        if (TripMetadataIdentity.Digest(identity.Contract, identity.Kind, identity.SubjectId, identity.Adapter, identity.TripId, bytes)
            != identity.RequestDigest)
            throw new TripMetadataMutationRejected(409, "trip-attempt-digest-mismatch", "The Trip save bytes do not match the issued proof.");
        if (bytes.AsSpan().StartsWith<byte>([0xEF, 0xBB, 0xBF]))
            throw new TripMetadataMutationRejected(400, "mutation-request-invalid", "Protected Trip saves cannot include a UTF-8 BOM.");
        return CommitAsync(identity.TripId, identity.SubjectId, permit, revalidateCredential, async (trip, readmission) =>
        {
            using var body = new MemoryStream(bytes, writable: false);
            if (identity.Kind != TripMetadataIdentity.OperationKind)
                return await ApplyControlAsync(trip, identity, await TripControlRequest.ReadAsync(body, identity, token), readmission, token);
            var request = await TripMetadataConditionalRequest.ReadAsync(body, identity.Adapter == "web", token, identity.Contract);
            return MetadataEffect(await CompareAndApplyAsync(trip, request, token), request, identity.Contract);
        }, token);
    }

    /// <summary>Compares the complete selection before applying any field or derived effect.</summary>
    private async Task<TripMetadataMutationResult> CompareAndApplyAsync(Trip trip, TripMetadataConditionalRequest request, CancellationToken token)
    {
        var current = Values(trip);
        if (request.Expected.Any(field => !Equals(field.Value, current[field.Key])))
            throw new TripMetadataMutationRejected(409, "mutation-base-stale",
                "The edited Trip fields changed. Keep your draft, review current values and explicitly choose a new base before saving.");
        return await ApplyFieldsAsync(trip, request, current, token);
    }

    /// <summary>Captures stored fields and any derived privacy effect for the original execution.</summary>
    private static TripMetadataExecution MetadataEffect(TripMetadataMutationResult mutation, TripMetadataConditionalRequest request, string? contract)
    {
        var state = mutation.Applied ? "applied" : "checkedNoOp";
        var evidence = new TripMetadataEvidence(state, state, false)
        {
            UpdatedAtUtc = mutation.Trip.UpdatedAt,
            FieldDigests = request.Changes.Keys.ToDictionary(field => field, field => TripMetadataResultDigests.For(field, mutation.Trip)),
            PrivacyEffect = contract == TripMetadataIdentity.Version2 && request.Changes.GetValueOrDefault("isPublic") is false
                ? new(false, mutation.DisabledShareProgress) : null
        };
        return new(mutation, contract == null ? null : evidence);
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
    /// <summary>Transport code used to select bounded terminal refusals.</summary>
    internal string Code => code;

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
