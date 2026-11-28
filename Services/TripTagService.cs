using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unidecode.NET;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services.TripMetadataReceipts;

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<TripTagDto>> AttachTagsAsync(Guid tripId, IEnumerable<string> names, string userId, CancellationToken cancellationToken = default) =>
        (await ChangeAsync(tripId, userId, "tags-attach", new(names.ToArray(), null, null), cancellationToken))
            .ResultingTags.OrderBy(tag => tag.Name).ToArray();

    /// <inheritdoc />
    public async Task<TripTagReplacementResult> ReplaceTagsAsync(Guid tripId, IReadOnlyList<string> names, string userId, CancellationToken cancellationToken = default)
    {
        var effect = await ChangeAsync(tripId, userId, "tags-replace", new(names.ToArray(), null, null), cancellationToken);
        return new(effect.ResultingTags.OrderBy(tag => tag.Name).ToArray(), effect.RemovedJoins.Select(tag => tag.Slug).ToArray());
    }

    /// <inheritdoc />
    public async Task<bool> DetachTagAsync(Guid tripId, string slug, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return false;
        try
        {
            await ChangeAsync(tripId, userId, "tags-detach", new([], null, slug), cancellationToken);
            return true;
        }
        catch (TripMetadataMutationRejected error) when (error.Code == "tag-not-on-trip") { return false; }
    }

    /// <summary>Retains the service entry points under the same membership locking rules.</summary>
    private async Task<TripTagEffect> ChangeAsync(Guid id, string owner, string kind, TripTagChange change, CancellationToken token)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(token);
        if (_dbContext.Database.IsRelational() && await TripTagMembership.LockTripAsync(_dbContext, id, owner, token) == null)
            throw new KeyNotFoundException("Trip not found or access denied.");
        var effect = await ApplyLockedAsync(id, owner, kind, change, () => Task.CompletedTask, token);
        await transaction.CommitAsync(token);
        return effect;
    }

    /// <summary>Compares, resolves and captures membership effects inside the caller's locked transaction.</summary>
    internal async Task<TripTagEffect> ApplyLockedAsync(Guid id, string owner, string kind, TripTagChange change,
        Func<Task> readmission, CancellationToken token)
    {
        var trip = await _dbContext.Trips.Include(t => t.Tags).SingleOrDefaultAsync(t => t.Id == id && t.UserId == owner, token)
            ?? throw new KeyNotFoundException("Trip not found or access denied.");
        var previous = trip.Tags.ToArray();
        if (change.Expected != null && !change.Expected.SetEquals(previous.Select(Identity)))
            throw new TripMetadataMutationRejected(409, "mutation-base-stale", "The Trip tag identities changed.");
        await TripTagMembership.LockCatalogueAsync(_dbContext, token);
        await readmission();
        var created = new List<Tag>();
        var next = await ResolveMembershipAsync(previous, kind, change, created, token);
        var added = next.ExceptBy(previous.Select(tag => tag.Id), tag => tag.Id).ToArray();
        var removed = previous.ExceptBy(next.Select(tag => tag.Id), tag => tag.Id).ToArray();
        await TripTagMembership.ProtectAsync(_dbContext, next.Except(created).Select(tag => tag.Id), token);
        await readmission();

        foreach (var tag in removed) trip.Tags.Remove(tag);
        foreach (var tag in added) trip.Tags.Add(tag);
        if (added.Length != 0 || removed.Length != 0)
        {
            await _dbContext.SaveChangesAsync(token);
            await TripTagMembership.TouchTripAsync(_dbContext, id, token);
        }
        var deleted = await TripTagMembership.DeleteUnusedAsync(_dbContext, removed.Select(tag => tag.Id).ToArray(), token);
        await readmission();
        return new(Ordered(added).Select(Identity).ToArray(), Ordered(removed).Select(Identity).ToArray(),
            Ordered(created).Select(Identity).ToArray(), Ordered(deleted).Select(Identity).ToArray(), Ordered(next).Select(MapToDto).ToArray());
    }

    /// <summary>Implements the distinct adapter normalization rules without changing attached identities silently.</summary>
    private async Task<Tag[]> ResolveMembershipAsync(Tag[] previous, string kind, TripTagChange change, List<Tag> created, CancellationToken token)
    {
        if (kind == "tags-detach")
        {
            if (!previous.Any(tag => tag.Slug == change.Slug))
                throw new TripMetadataMutationRejected(404, "tag-not-on-trip", "Tag not found on this Trip.");
            return previous.Where(tag => tag.Slug != change.Slug).ToArray();
        }
        var attach = kind == "tags-attach";
        var names = attach ? change.Names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : change.Names.Select(name => name.Trim()).ToArray();
        if (attach && names.Length == 0) throw new ValidationException("Please provide at least one tag.");
        foreach (var name in names) ValidateTagName(name);
        var desired = names.Select(name => (Name: name.Normalize(NormalizationForm.FormC), Slug: ToSlug(name)))
            .DistinctBy(name => attach ? name.Name : name.Slug).OrderBy(name => name.Slug, StringComparer.Ordinal);
        var next = attach ? previous.ToList() : new List<Tag>();
        foreach (var (name, slug) in desired)
        {
            var tag = await ResolveAsync(name, slug, attach, created, token);
            if (!next.Any(current => current.Id == tag.Id)) next.Add(tag);
        }
        if (next.Count > MaxTagsPerTrip) throw new ValidationException("You can add up to 25 tags per trip.");
        return next.ToArray();
    }

    /// <summary>Resolves name first on attach and slug first on replacement, including a unique-key winner.</summary>
    private async Task<Tag> ResolveAsync(string name, string slug, bool byName, List<Tag> created, CancellationToken token)
    {
        var matches = await _dbContext.Tags.Where(tag => tag.Name == name || tag.Slug == slug).ToListAsync(token);
        var existing = byName ? matches.FirstOrDefault(tag => tag.Name == name) : matches.FirstOrDefault(tag => tag.Slug == slug);
        if (_dbContext.Database.IsRelational()) existing ??= matches.FirstOrDefault();
        if (existing != null) return existing;
        if (_dbContext.Database.IsRelational())
        {
            var id = Guid.NewGuid();
            var count = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."Tags" ("Id", "Name", "Slug") VALUES ({id}, {name}, {slug}) ON CONFLICT DO NOTHING
                """, token);
            if (count == 0)
            {
                logger.LogDebug("Resolving the committed tag catalogue winner.");
                var winners = await _dbContext.Tags.Where(tag => tag.Name == name || tag.Slug == slug).ToListAsync(token);
                var winner = byName ? winners.FirstOrDefault(tag => tag.Name == name) : winners.FirstOrDefault(tag => tag.Slug == slug);
                return winner ?? winners.SingleOrDefault() ?? throw TripMetadataMutationService.Contention();
            }
            var inserted = await _dbContext.Tags.SingleAsync(tag => tag.Id == id, token);
            created.Add(inserted);
            return inserted;
        }
        var candidate = new Tag { Id = Guid.NewGuid(), Name = name, Slug = slug };
        _dbContext.Tags.Add(candidate);
        created.Add(candidate);
        return candidate;
    }

    /// <summary>Sorts every evidence array by ordinal slug, then canonical UUID.</summary>
    private static IEnumerable<Tag> Ordered(IEnumerable<Tag> tags) =>
        tags.OrderBy(tag => tag.Slug, StringComparer.Ordinal).ThenBy(tag => tag.Id.ToString("D"), StringComparer.Ordinal);

    /// <summary>Projects the stored identity without re-normalizing it.</summary>
    private static TripTagIdentity Identity(Tag tag) => new(tag.Id, tag.Slug);

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

    /// <inheritdoc />
    public async Task RemoveOrphanTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await TripTagMembership.LockCatalogueAsync(_dbContext, cancellationToken);
        var candidates = await _dbContext.Tags.Where(tag => !tag.Trips.Any()).Select(tag => tag.Id).ToArrayAsync(cancellationToken);
        await TripTagMembership.DeleteUnusedAsync(_dbContext, candidates, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static string NormalizeSlug(string name) => ToSlug(name);

    public static bool IsValidTagName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && NameRegex.IsMatch(name);

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

}
