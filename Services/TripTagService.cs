using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Unidecode.NET;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>
/// Concrete implementation handling tag creation, attachment, cleanup, and query helpers.
/// </summary>
public sealed class TripTagService(ApplicationDbContext dbContext, ILogger<TripTagService> logger)
    : ITripTagService
{
    private static readonly Regex NameRegex = new(@"^[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-'']*$", RegexOptions.Compiled);
    private const int MaxTagsPerTrip = 25;

    public async Task<IReadOnlyList<TripTagDto>> GetTagsForTripAsync(Guid tripId, string userId, CancellationToken cancellationToken = default)
    {
        var trip = await _dbContext.Trips
            .Include(t => t.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

        if (trip == null)
        {
            throw new KeyNotFoundException("Trip not found or access denied.");
        }

        return trip.Tags
            .OrderBy(t => t.Name)
            .Select(MapToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<TripTagDto>> AttachTagsAsync(Guid tripId, IEnumerable<string> names, string userId, CancellationToken cancellationToken = default)
    {
        var normalizedNames = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedNames.Length == 0)
        {
            throw new ValidationException("Please provide at least one tag.");
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var trip = await _dbContext.Trips
            .Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

        if (trip == null)
        {
            throw new KeyNotFoundException("Trip not found or access denied.");
        }

        var pending = new List<Tag>();

        foreach (var rawName in normalizedNames)
        {
            ValidateTagName(rawName);

            var tag = await GetOrCreateTagAsync(rawName, cancellationToken);
            if (!trip.Tags.Any(t => t.Id == tag.Id))
            {
                pending.Add(tag);
            }
        }

        if (trip.Tags.Count + pending.Count > MaxTagsPerTrip)
        {
            throw new ValidationException("You can add up to 25 tags per trip.");
        }

        foreach (var tag in pending)
        {
            trip.Tags.Add(tag);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return trip.Tags
            .OrderBy(t => t.Name)
            .Select(MapToDto)
            .ToList();
    }

    public async Task<TripTagReplacementResult> ReplaceTagsAsync(Guid tripId, IReadOnlyList<string> names, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var desired = names
            .Select(name => new { Name = name.Trim().Normalize(NormalizationForm.FormC), Slug = ToSlug(name) })
            .DistinctBy(tag => tag.Slug)
            .ToList();

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var trip = await _dbContext.Trips
            .Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

        if (trip == null)
        {
            throw new KeyNotFoundException("Trip not found or access denied.");
        }

        var previousSlugs = trip.Tags.Select(t => t.Slug).ToHashSet(StringComparer.Ordinal);
        var desiredSlugs = desired.Select(t => t.Slug).ToHashSet(StringComparer.Ordinal);
        var deletedSlugs = previousSlugs.Except(desiredSlugs, StringComparer.Ordinal).OrderBy(slug => slug).ToList();
        var nextTags = new List<Tag>();

        foreach (var desiredTag in desired)
        {
            ValidateTagName(desiredTag.Name);
            nextTags.Add(await GetOrCreateTagBySlugAsync(desiredTag.Name, desiredTag.Slug, cancellationToken));
        }

        trip.Tags.Clear();
        foreach (var tag in nextTags)
        {
            trip.Tags.Add(tag);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await RemoveUnusedTagsAsync(deletedSlugs, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var tags = await GetTagsForTripAsync(tripId, userId, cancellationToken);
        return new TripTagReplacementResult(tags, deletedSlugs);
    }

    /// <summary>Resolves the slug-only tag values written by the Wayfarer KML exporter.</summary>
    public async Task<IReadOnlyList<Tag>> ReconcileImportedTagsAsync(IEnumerable<string> tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var canonicalSlugs = new List<string>();

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (!TryGetImportSlug(token, out var slug))
            {
                _logger.LogWarning("KML import tag token cannot be represented safely: {TagToken}", token);
                throw new TripImportValidationException("The import contains an invalid tag.");
            }

            if (!canonicalSlugs.Contains(slug, StringComparer.Ordinal))
            {
                canonicalSlugs.Add(slug);
            }
        }

        if (canonicalSlugs.Count > MaxTagsPerTrip)
        {
            throw new TripImportValidationException("The import contains too many tags.");
        }

        var resolved = new List<Tag>(canonicalSlugs.Count);
        foreach (var slug in canonicalSlugs)
        {
            resolved.Add(await ResolveImportedTagAsync(slug, cancellationToken));
        }

        return resolved;
    }

    public async Task<bool> DetachTagAsync(Guid tripId, string slug, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var trip = await _dbContext.Trips
            .Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

        if (trip == null)
        {
            throw new KeyNotFoundException("Trip not found or access denied.");
        }

        var tag = trip.Tags.FirstOrDefault(t => t.Slug == slug);
        if (tag == null)
        {
            return false;
        }

        trip.Tags.Remove(tag);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var stillUsed = await _dbContext.Entry(tag)
            .Collection(t => t.Trips)
            .Query()
            .AnyAsync(cancellationToken);

        if (!stillUsed)
        {
            _dbContext.Tags.Remove(tag);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<TagSuggestionDto>> GetSuggestionsAsync(string? query, int limit = 10, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var trimmedQuery = query?.Trim();

        // Use raw SQL to avoid EF Core translation issues with Dictionary join entity
        string sql;
        List<TagSuggestionDto> tags;

        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            sql = @"
                SELECT t.""Name"", t.""Slug"", COUNT(*)::int as ""Count""
                FROM ""Tags"" t
                INNER JOIN ""TripTags"" tt ON t.""Id"" = tt.""TagId""
                INNER JOIN ""Trips"" trip ON tt.""TripId"" = trip.""Id""
                WHERE trip.""IsPublic"" = true
                GROUP BY t.""Id"", t.""Name"", t.""Slug""
                ORDER BY COUNT(*) DESC, t.""Name""
                LIMIT {0}";

            tags = await _dbContext.Database
                .SqlQueryRaw<TagSuggestionDto>(sql, limit)
                .ToListAsync(cancellationToken);
        }
        else
        {
            sql = @"
                SELECT t.""Name"", t.""Slug"", COUNT(*)::int as ""Count""
                FROM ""Tags"" t
                INNER JOIN ""TripTags"" tt ON t.""Id"" = tt.""TagId""
                INNER JOIN ""Trips"" trip ON tt.""TripId"" = trip.""Id""
                WHERE trip.""IsPublic"" = true
                  AND t.""Name"" ILIKE {0}
                GROUP BY t.""Id"", t.""Name"", t.""Slug""
                ORDER BY COUNT(*) DESC, t.""Name""
                LIMIT {1}";

            tags = await _dbContext.Database
                .SqlQueryRaw<TagSuggestionDto>(sql, $"%{trimmedQuery}%", limit)
                .ToListAsync(cancellationToken);
        }

        return tags;
    }

    public async Task<IReadOnlyList<TagSuggestionDto>> GetPopularAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 50);

        // Use raw SQL to avoid EF Core translation issues with Dictionary join entity
        var sql = @"
            SELECT t.""Name"", t.""Slug"", COUNT(*)::int as ""Count""
            FROM ""Tags"" t
            INNER JOIN ""TripTags"" tt ON t.""Id"" = tt.""TagId""
            INNER JOIN ""Trips"" trip ON tt.""TripId"" = trip.""Id""
            WHERE trip.""IsPublic"" = true
            GROUP BY t.""Id"", t.""Name"", t.""Slug""
            ORDER BY COUNT(*) DESC, t.""Name""
            LIMIT {0}";

        var items = await _dbContext.Database
            .SqlQueryRaw<TagSuggestionDto>(sql, take)
            .ToListAsync(cancellationToken);

        return items;
    }

    public IQueryable<Trip> ApplyTagFilter(IQueryable<Trip> query, IReadOnlyCollection<string> slugs, string mode)
    {
        if (slugs == null || slugs.Count == 0)
        {
            return query;
        }

        var slugList = slugs
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (slugList.Length == 0)
        {
            return query;
        }

        if (string.Equals(mode, "any", StringComparison.OrdinalIgnoreCase))
        {
            return query.Where(t => t.Tags.Any(tt => slugList.Contains(tt.Slug)));
        }

        var slugCount = slugList.Length;
        return query.Where(t =>
            t.Tags.Where(tt => slugList.Contains(tt.Slug))
                  .Select(tt => tt.Slug)
                  .Distinct()
                  .Count() == slugCount);
    }

    public async Task RemoveOrphanTagsAsync(CancellationToken cancellationToken = default)
    {
        await _dbContext.Database.ExecuteSqlRawAsync(@"
DELETE FROM ""Tags"" t
WHERE NOT EXISTS (
    SELECT 1 FROM ""TripTags"" tt WHERE tt.""TagId"" = t.""Id""
);", cancellationToken);
    }

    public static string NormalizeSlug(string name) => ToSlug(name);

    public static bool IsValidTagName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && NameRegex.IsMatch(name);

    /// <summary>Derives an import identity without the random fallback used by tag editing.</summary>
    public static bool TryGetImportSlug(string token, out string slug)
    {
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var candidate = ToSlugWithoutFallback(token.Trim().Normalize(NormalizationForm.FormC));
        if (string.IsNullOrEmpty(candidate) || candidate.Length > 200 || !IsValidTagName(candidate))
        {
            return false;
        }

        slug = candidate;
        return true;
    }

    private static void ValidateTagName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("Tag cannot be empty.");
        }

        if (name.Length > 64)
        {
            throw new ValidationException("Tag too long (max 64 characters).");
        }

        if (!NameRegex.IsMatch(name))
        {
            throw new ValidationException("Tags may include letters, numbers, spaces, hyphen, apostrophe.");
        }
    }

    private async Task<Tag> GetOrCreateTagAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var existing = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Name == normalized, cancellationToken);
        if (existing != null)
        {
            return existing;
        }

        var tag = new Tag
        {
            Name = normalized.Normalize(NormalizationForm.FormC),
            Slug = ToSlug(normalized)
        };

        _dbContext.Tags.Add(tag);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return tag;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Race while creating tag {Tag}", normalized);
            return await _dbContext.Tags.FirstAsync(t => t.Name == normalized, cancellationToken);
        }
    }

    private async Task<Tag> GetOrCreateTagBySlugAsync(string name, string slug, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        if (existing != null)
        {
            return existing;
        }

        var tag = new Tag
        {
            Name = name,
            Slug = slug
        };

        _dbContext.Tags.Add(tag);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return tag;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Race while creating tag slug {TagSlug}", slug);
            return await _dbContext.Tags.FirstAsync(t => t.Slug == slug, cancellationToken);
        }
    }

    private async Task<Tag> ResolveImportedTagAsync(string slug, CancellationToken cancellationToken)
    {
        var bySlug = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        if (bySlug is not null)
        {
            return bySlug;
        }

        // KML carries no display name, so use its canonical slug without title casing.
        var byName = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Name == slug, cancellationToken);
        if (byName is not null)
        {
            _logger.LogWarning("KML import tag identity conflicts for {TagSlug}: name belongs to {TagId}", slug, byName.Id);
            throw new TripImportValidationException("The import contains an invalid tag.");
        }

        var candidate = new Tag { Name = slug, Slug = slug };
        _dbContext.Tags.Add(candidate);
        if (!_dbContext.Database.IsRelational())
        {
            // The normal unit-test provider cannot model database unique races or transactions.
            return candidate;
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return candidate;
        }
        catch (DbUpdateException ex) when (IsRecognizedTagUniqueConflict(ex))
        {
            _dbContext.Entry(candidate).State = EntityState.Detached;
            _logger.LogInformation(ex, "Recognized concurrent KML import tag create for {TagSlug}", slug);

            var winnerBySlug = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
            var winnerByName = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Name == slug, cancellationToken);
            if (winnerBySlug is not null && winnerByName is not null && winnerBySlug.Id == winnerByName.Id)
            {
                return winnerBySlug;
            }

            _logger.LogWarning("KML import tag conflict has no single winner for {TagSlug}; slug={SlugTagId}, name={NameTagId}", slug, winnerBySlug?.Id, winnerByName?.Id);
            throw new TripImportValidationException("The import contains an invalid tag.");
        }
    }

    private async Task RemoveUnusedTagsAsync(IReadOnlyList<string> slugs, CancellationToken cancellationToken)
    {
        if (slugs.Count == 0)
        {
            return;
        }

        var candidates = await _dbContext.Tags
            .Include(t => t.Trips)
            .Where(t => slugs.Contains(t.Slug))
            .ToListAsync(cancellationToken);

        foreach (var tag in candidates.Where(t => t.Trips.Count == 0))
        {
            _dbContext.Tags.Remove(tag);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static TripTagDto MapToDto(Tag tag) => new(tag.Id, tag.Name, tag.Slug);

    private static string ToSlug(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        var slug = ToSlugWithoutFallback(trimmed);
        if (!string.IsNullOrEmpty(slug))
        {
            return slug;
        }

        var randomSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"tag-{randomSuffix}";
    }

    private static string ToSlugWithoutFallback(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormD);
        var ascii = normalized.Unidecode();

        var sb = new StringBuilder();
        foreach (var ch in ascii)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        return sb.ToString().Trim('-');
    }

    private static bool IsRecognizedTagUniqueConflict(DbUpdateException exception) =>
        exception.GetBaseException() is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Tags_Slug" or "IX_Tags_Name"
        };

    private readonly ApplicationDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<TripTagService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
}
