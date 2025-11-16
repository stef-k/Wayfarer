using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private static readonly Regex NameRegex = new(@"^[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-'’]*$", RegexOptions.Compiled);
    private const int MaxTagsPerTrip = 15;

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
            throw new ValidationException("You can add up to 15 tags per trip.");
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

    private void ValidateTagName(string name)
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

    private static TripTagDto MapToDto(Tag tag) => new(tag.Id, tag.Name, tag.Slug);

    private static string ToSlug(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        var normalized = trimmed.Normalize(NormalizationForm.FormD);
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

        var slug = sb.ToString().Trim('-');
        if (!string.IsNullOrEmpty(slug))
        {
            return slug;
        }

        var randomSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"tag-{randomSuffix}";
    }

    private readonly ApplicationDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<TripTagService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
}
