using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>
/// Trip-level tags and share-progress mutations for the private Vue Trip Editor.
/// </summary>
public sealed partial class TripEditorController
{
    /// <summary>
    /// Replaces the complete trip-level tag set for an owned trip.
    /// </summary>
    [HttpPut("tags")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PutTags(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        if (!await _dbContext.Trips.AnyAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken))
        {
            return NotFound();
        }

        var request = await TryReadJsonRequestAsync(cancellationToken);
        if (!request.HasValue)
        {
            return ValidationError(new Dictionary<string, string[]> { ["request"] = new[] { "Tag update request must be valid JSON." } });
        }

        if (!TryParseTagsRequest(request.Value, BuildOptions().Tag.MaxTags, out var update, out var errors))
        {
            return ValidationError(errors);
        }

        try
        {
            var replacement = await _tripTagService.ReplaceTagsAsync(tripId, update.Tags, userId!, cancellationToken);
            var tags = replacement.Tags.Select(ToEditorTag).ToList();
            var affected = new EditorAffectedSlicesDto(
                null,
                Array.Empty<EditorRegionDto>(),
                null,
                Array.Empty<EditorPlaceDto>(),
                new Dictionary<Guid, IReadOnlyList<Guid>>(),
                Array.Empty<EditorAreaDto>(),
                new Dictionary<Guid, IReadOnlyList<Guid>>(),
                Array.Empty<EditorSegmentDto>(),
                null,
                tags,
                tags.Select(t => t.Slug).ToList(),
                null,
                null);
            var deletedIds = new EditorDeletedIdsDto(Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), replacement.DeletedSlugs);

            return Ok(new EditorMutationResult<IReadOnlyList<EditorTagDto>>(true, tags, affected, deletedIds, Array.Empty<EditorWarningDto>()));
        }
        catch (ValidationException)
        {
            return ValidationError(new Dictionary<string, string[]> { ["tags"] = new[] { "One or more tags are invalid." } });
        }
    }

    /// <summary>
    /// Toggles public visit-progress sharing for an owned trip.
    /// </summary>
    [HttpPatch("share-progress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchShareProgress(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var trip = await _dbContext.Trips.FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);
        if (trip == null)
        {
            return NotFound();
        }

        var request = await TryReadJsonRequestAsync(cancellationToken);
        if (!request.HasValue)
        {
            return ValidationError(new Dictionary<string, string[]> { ["request"] = new[] { "Share-progress update request must be valid JSON." } });
        }

        if (!TryParseShareProgressRequest(request.Value, out var update, out var errors))
        {
            return ValidationError(errors);
        }

        if (update.Enabled && !trip.IsPublic)
        {
            return ValidationError(new Dictionary<string, string[]>
            {
                ["shareProgressEnabled"] = new[] { "Share progress can only be enabled for public trips." }
            });
        }

        trip.ShareProgressEnabled = update.Enabled;
        trip.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var metadata = EditorTripStateMapper.ToMetadata(
            trip,
            trip.IsPublic ? GeneratePublicTripUrl(trip.Id) : null,
            trip.IsPublic && trip.ShareProgressEnabled ? GenerateProgressPublicTripUrl(trip.Id) : null);

        return Ok(new EditorMutationResult<EditorTripMetadataDto>(
            true,
            metadata,
            EditorAffectedSlicesDto.MetadataOnly(metadata),
            EditorDeletedIdsDto.Empty,
            Array.Empty<EditorWarningDto>()));
    }

    private async Task<JsonElement?> TryReadJsonRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseTagsRequest(
        JsonElement request,
        int maxTags,
        out EditorTripTagsUpdateRequest update,
        out Dictionary<string, string[]> errors)
    {
        update = new EditorTripTagsUpdateRequest(Array.Empty<string>());
        errors = new Dictionary<string, string[]>();
        if (request.ValueKind != JsonValueKind.Object)
        {
            errors["request"] = new[] { "Tag update request must be a JSON object." };
            return false;
        }

        if (!request.TryGetProperty("tags", out var tagsElement) || tagsElement.ValueKind != JsonValueKind.Array)
        {
            errors["tags"] = new[] { "Tags must be provided as an array." };
            return false;
        }

        var tags = new List<string>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var tagElement in tagsElement.EnumerateArray())
        {
            if (tagElement.ValueKind != JsonValueKind.String)
            {
                errors[$"tags[{index}]"] = new[] { "Tag value must be a string." };
                index++;
                continue;
            }

            var tag = tagElement.GetString()?.Trim() ?? string.Empty;
            if (tag.Length == 0)
            {
                errors[$"tags[{index}]"] = new[] { "Tag value cannot be blank." };
                index++;
                continue;
            }

            if (!TripTagService.IsValidTagName(tag))
            {
                errors[$"tags[{index}]"] = new[] { "Tag uses unsupported characters or is too long." };
                index++;
                continue;
            }

            if (slugs.Add(TripTagService.NormalizeSlug(tag)))
            {
                tags.Add(tag);
            }

            index++;
        }

        if (tags.Count > maxTags)
        {
            errors["tags"] = new[] { $"A trip can have up to {maxTags} tags." };
        }

        update = new EditorTripTagsUpdateRequest(tags);
        return errors.Count == 0;
    }

    private static bool TryParseShareProgressRequest(
        JsonElement request,
        out EditorShareProgressUpdateRequest update,
        out Dictionary<string, string[]> errors)
    {
        update = new EditorShareProgressUpdateRequest(false);
        errors = new Dictionary<string, string[]>();
        if (request.ValueKind != JsonValueKind.Object)
        {
            errors["request"] = new[] { "Share-progress update request must be a JSON object." };
            return false;
        }

        if (!request.TryGetProperty("enabled", out var enabledElement))
        {
            errors["enabled"] = new[] { "Enabled is required." };
            return false;
        }

        if (enabledElement.ValueKind != JsonValueKind.True && enabledElement.ValueKind != JsonValueKind.False)
        {
            errors["enabled"] = new[] { "Enabled must be a boolean." };
            return false;
        }

        update = new EditorShareProgressUpdateRequest(enabledElement.GetBoolean());
        return true;
    }

    private static EditorTagDto ToEditorTag(TripTagDto tag) => new(tag.Id, tag.Name, tag.Slug);
}
