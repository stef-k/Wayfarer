using System.Globalization;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Derives stable episode identity and validates the chronology of reactivation.</summary>
internal static class GroupMembershipEpisode
{
    /// <summary>PostgreSQL stores timestamps at microsecond precision.</summary>
    internal static DateTime AtStoredPrecision(DateTime utc) => utc.AddTicks(-(utc.Ticks % 10));

    /// <summary>The member row is reused across episodes and its stored join instant distinguishes them.</summary>
    internal static string Reference(GroupMember member) =>
        member.Id.ToString("N") + "." + AtStoredPrecision(member.JoinedAt).Ticks.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>Only a later invitation and a strictly later stored join instant permit reactivation.</summary>
    internal static bool CanAdmit(GroupMember? member, DateTime invitedAt, DateTime now) => member is null
        || (member.Status is GroupMember.MembershipStatuses.Left or GroupMember.MembershipStatuses.Removed
            && member.JoinedAt != default && member.LeftAt is { } left && left >= member.JoinedAt
            && invitedAt > left && AtStoredPrecision(now) > AtStoredPrecision(left));
}
