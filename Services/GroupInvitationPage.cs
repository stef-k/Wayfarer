using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Opaque continuation and safe projections for the two mobile invitation lists.</summary>
internal sealed class GroupInvitationPage(IDataProtectionProvider protection)
{
    private const int PageSize = 50;
    private readonly IDataProtector _protector = protection.CreateProtector("Wayfarer.GroupInvitations.Continuation.v1");

    /// <summary>A null Group identifies the recipient inbox, while a Group ID identifies its manager list.</summary>
    internal sealed record Cursor(string Actor, Guid? GroupId, DateTime CreatedAt, Guid Id);

    /// <summary>Checks protected syntax independently of its current authorized scope.</summary>
    internal bool TryDecode(string? value, out Cursor? cursor)
    {
        cursor = null;
        if (value is null) return true;
        if (value.Length is 0 or > 4096) return false;
        try
        {
            cursor = JsonSerializer.Deserialize<Cursor>(_protector.Unprotect(value));
            return cursor is { Actor.Length: > 0 };
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }

    /// <summary>Returns one authorized page using stored creation time and ID as the keyset.</summary>
    internal async Task<object> ReadAsync(IQueryable<GroupInvitation> live, string actor, Guid? groupId, Cursor? cursor,
        CancellationToken ct)
    {
        if (cursor is not null && (cursor.Actor != actor || cursor.GroupId != groupId))
            throw new GroupRefusal(400, "cursor_scope_mismatch");

        if (cursor is not null)
            live = live.Where(i => i.CreatedAt < cursor.CreatedAt
                || i.CreatedAt == cursor.CreatedAt && i.Id.CompareTo(cursor.Id) < 0);
        var page = live.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Take(PageSize + 1);
        if (groupId.HasValue)
        {
            var rows = await page.Select(i => new ManagerItem(i.Id, i.GroupId, i.Status, i.CreatedAt, i.ExpiresAt,
                i.InviteeUserId!, i.Invitee!.UserName, i.Invitee.DisplayName)).ToListAsync(ct);
            return Payload(rows, actor, groupId);
        }
        return Payload(await RecipientItems(page).ToListAsync(ct), actor, groupId);
    }

    /// <summary>Projects only fields that the addressed account may see, even before admission.</summary>
    internal static IQueryable<RecipientItem> RecipientItems(IQueryable<GroupInvitation> invitations) =>
        invitations.Select(i => new RecipientItem(i.Id, i.GroupId, i.Status, i.CreatedAt, i.ExpiresAt,
            i.Group!.Name, i.Group.GroupType, i.Inviter!.UserName, i.Inviter.DisplayName));

    /// <summary>Trims the lookahead and derives the continuation from the last delivered row.</summary>
    private object Payload<T>(List<T> rows, string actor, Guid? groupId) where T : Item
    {
        string? nextCursor = null;
        if (rows.Count > PageSize)
        {
            rows.RemoveAt(PageSize);
            var last = rows[^1];
            nextCursor = _protector.Protect(JsonSerializer.Serialize(new Cursor(actor, groupId, last.CreatedAtUtc, last.Id)));
        }
        return new { items = rows, nextCursor };
    }

    /// <summary>Common invitation identifiers and lifecycle fields exposed by both lists.</summary>
    internal abstract record Item(Guid Id, Guid GroupId, string Status, DateTime CreatedAtUtc, DateTime? ExpiresAtUtc);

    /// <summary>A pending registered-account invitation visible to Group management.</summary>
    internal sealed record ManagerItem(Guid Id, Guid GroupId, string Status, DateTime CreatedAtUtc, DateTime? ExpiresAtUtc,
        string InviteeUserId, string? UserName, string? DisplayName) : Item(Id, GroupId, Status, CreatedAtUtc, ExpiresAtUtc);

    /// <summary>A pending invitation visible to its addressed account.</summary>
    internal sealed record RecipientItem(Guid Id, Guid GroupId, string Status, DateTime CreatedAtUtc, DateTime? ExpiresAtUtc,
        string GroupName, string? StoredGroupType, string? InviterUserName, string? InviterDisplayName)
        : Item(Id, GroupId, Status, CreatedAtUtc, ExpiresAtUtc)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public string? StoredGroupType { get; init; } = StoredGroupType;
        public string? GroupType => GroupSharingPolicy.CanonicalType(StoredGroupType);
    }
}
