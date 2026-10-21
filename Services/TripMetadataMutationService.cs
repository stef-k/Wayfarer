using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>One PostgreSQL compare/apply boundary for protected bearer and cookie Trip metadata edits.</summary>
internal sealed class TripMetadataMutationService(ApplicationDbContext db)
{
    /// <summary>Compares pinned originals after the final row lock; writes only dirty fields and necessary derived effects.</summary>
    internal async Task<TripMetadataMutationResult> ApplyAsync(Guid tripId, string userId, Stream body, bool web,
        Func<CancellationToken, Task<bool>>? revalidateBearer, CancellationToken token)
    {
        if (!await db.Trips.AsNoTracking().AnyAsync(t => t.Id == tripId && t.UserId == userId, token))
            throw new TripMetadataMutationRejected(404, "mutation-scope-unavailable", "Trip not found.");
        var request = await TripMetadataConditionalRequest.ReadAsync(body, web, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var commitStarted = false;
        try
        {
            // FromSql + AsNoTracking obtains the post-wait row, independent of any earlier tracked/preflight state.
            var trip = await db.Trips.FromSqlInterpolated($"SELECT * FROM public.\"Trips\" WHERE \"Id\" = {tripId} AND \"UserId\" = {userId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (trip == null) throw new TripMetadataMutationRejected(404, "mutation-scope-unavailable", "Trip not found.");
            var active = await db.Users.FromSqlInterpolated($"SELECT * FROM public.\"AspNetUsers\" WHERE \"Id\" = {userId} AND \"IsActive\" FOR SHARE")
                .AsNoTracking().ToListAsync(token);
            if (active.Count == 0 || revalidateBearer != null && !await revalidateBearer(token))
                throw new TripMetadataMutationRejected(401, "mutation-authority-unavailable", "Authentication is no longer valid.");

            var current = Values(trip);
            if (request.Expected.Any(field => !Equals(field.Value, current[field.Key])))
                throw new TripMetadataMutationRejected(409, "mutation-base-stale",
                    "The edited Trip fields changed. Keep your draft, review current values and explicitly choose a new base before saving.");

            var beforeImages = Images(trip);
            var disableSharing = request.Changes.TryGetValue("isPublic", out var publication) && publication is false && trip.ShareProgressEnabled;
            var applied = request.Changes.Any(field => !Equals(field.Value, field.Key == "notes" ? trip.Notes : current[field.Key])) || disableSharing;
            if (applied)
            {
                var now = DateTime.UtcNow;
                trip.UpdatedAt = trip.UpdatedAt > now ? trip.UpdatedAt : now;
                await WriteAsync(trip, request, disableSharing, token);
            }
            var afterImages = Images(trip);
            var result = new TripMetadataMutationResult(trip, applied, disableSharing,
                request.Changes.ContainsKey("notes"), afterImages.Any(url => !beforeImages.Contains(url)), beforeImages.Count == 0 && afterImages.Count > 0);
            commitStarted = true;
            await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception original)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollbackFailure) { throw new AggregateException("Trip metadata failed and rollback also failed.", original, rollbackFailure); }
            // Commit/reply errors and cancellation remain uncertain failures, never a definitive no-effect conflict.
            if (!commitStarted && original is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
                throw new TripMetadataMutationRejected(409, "mutation-contention", "Trip editing contention prevented this attempt. Review current values before saving.");
            throw;
        }
    }

    /// <summary>Uses dynamic property setters without attaching a stale Trip or saving unrelated tracked work.</summary>
    private async Task WriteAsync(Trip trip, TripMetadataConditionalRequest request, bool disableSharing, CancellationToken token)
    {
        await db.Trips.Where(t => t.Id == trip.Id && t.UserId == trip.UserId).ExecuteUpdateAsync(setters =>
        {
            foreach (var (key, value) in request.Changes)
            {
                switch (key)
                {
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
        ["name"] = trip.Name, ["notes"] = RichNotes.Normalize(trip.Notes), ["isPublic"] = trip.IsPublic,
        ["coverImage"] = trip.CoverImageUrl, ["center"] = new TripMetadataCenter(trip.CenterLat, trip.CenterLon), ["zoom"] = trip.Zoom
    };

    /// <summary>Compares external image identity for the existing post-commit warm-up gates.</summary>
    private static HashSet<string> Images(Trip trip) => HtmlHelpers.ExtractExternalImageUrls(trip.Notes)
        .Concat(string.IsNullOrWhiteSpace(trip.CoverImageUrl) ? [] : new[] { trip.CoverImageUrl.Trim() })
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Confirmed committed result; conveys no idempotency or durable receipt guarantee.</summary>
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
