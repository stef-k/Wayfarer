using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Unidecode.NET;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Reconciles slug-only KML tags against Wayfarer's global tag identities.</summary>
public sealed class TripImportTagReconciler(ApplicationDbContext dbContext, ILogger<TripImportTagReconciler> logger)
    : ITripImportTagReconciler
{
    private const int MaxTagsPerTrip = 25;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Tag>> ReconcileAsync(IEnumerable<string> tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var slugs = new List<string>();
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (!TryGetCanonicalSlug(token, out var slug))
            {
                logger.LogWarning("KML import tag token cannot be represented safely: {TagToken}", token);
                throw new TripImportValidationException("The import contains an invalid tag.");
            }
            if (!slugs.Contains(slug, StringComparer.Ordinal)) slugs.Add(slug);
        }

        if (slugs.Count > MaxTagsPerTrip)
            throw new TripImportValidationException("The import contains too many tags.");

        var tags = new List<Tag>(slugs.Count);
        foreach (var slug in slugs)
            tags.Add(await ResolveAsync(slug, cancellationToken));
        return tags;
    }

    /// <summary>Derives a nonempty canonical import identity without a random fallback.</summary>
    internal static bool TryGetCanonicalSlug(string token, out string slug)
    {
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var normalized = token.Trim().Normalize(NormalizationForm.FormC);
        var ascii = normalized.Normalize(NormalizationForm.FormD).Unidecode();
        var builder = new StringBuilder();
        foreach (var character in ascii)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var candidate = builder.ToString().Trim('-');
        if (string.IsNullOrEmpty(candidate) || candidate.Length > 64 || !TripTagService.IsValidTagName(candidate)) return false;
        slug = candidate;
        return true;
    }

    private async Task<Tag> ResolveAsync(string slug, CancellationToken cancellationToken)
    {
        var bySlug = await dbContext.Tags.FirstOrDefaultAsync(tag => tag.Slug == slug, cancellationToken);
        if (bySlug is not null) return bySlug;

        var byName = await dbContext.Tags.FirstOrDefaultAsync(tag => tag.Name == slug, cancellationToken);
        if (byName is not null)
        {
            logger.LogWarning("KML tag identity conflicts for {TagSlug}: name belongs to {TagId}", slug, byName.Id);
            throw new TripImportValidationException("The import contains an invalid tag.");
        }

        var candidate = new Tag { Name = slug, Slug = slug };
        dbContext.Tags.Add(candidate);
        if (!dbContext.Database.IsRelational()) return candidate;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return candidate;
        }
        catch (DbUpdateException ex) when (IsRecognizedTagUniqueConflict(ex))
        {
            dbContext.Entry(candidate).State = EntityState.Detached;
            logger.LogInformation(ex, "Recognized concurrent KML import tag create for {TagSlug}", slug);
            var winnerBySlug = await dbContext.Tags.FirstOrDefaultAsync(tag => tag.Slug == slug, cancellationToken);
            var winnerByName = await dbContext.Tags.FirstOrDefaultAsync(tag => tag.Name == slug, cancellationToken);
            if (winnerBySlug is not null && winnerByName is not null && winnerBySlug.Id == winnerByName.Id) return winnerBySlug;

            logger.LogWarning("KML tag conflict has no single winner for {TagSlug}; slug={SlugTagId}, name={NameTagId}", slug, winnerBySlug?.Id, winnerByName?.Id);
            throw new TripImportValidationException("The import contains an invalid tag.");
        }
    }

    private static bool IsRecognizedTagUniqueConflict(DbUpdateException exception) =>
        exception.GetBaseException() is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Tags_Slug" or "IX_Tags_Name"
        };
}
