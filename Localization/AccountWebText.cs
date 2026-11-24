using Microsoft.AspNetCore.Mvc.Rendering;
using Wayfarer.Util;

namespace Wayfarer.Localization;

/// <summary>Maps installation roles to display text without changing any role value.</summary>
public static class AccountWebText
{
    /// <summary>Localizes built-in roles and displays custom role names verbatim.</summary>
    public static string Role(WebMessages text, string? role) => role switch
    {
        ApplicationRoles.Admin => text["Accounts.RoleAdmin"],
        ApplicationRoles.Manager => text["Accounts.RoleManager"],
        ApplicationRoles.User => text["Accounts.RoleUser"],
        null or "" => text["Accounts.NoRole"],
        _ => role
    };

    /// <summary>Preserves the value and selection of each admitted role option.</summary>
    public static IEnumerable<SelectListItem> RoleOptions(WebMessages text, IEnumerable<SelectListItem> items) =>
        items.Select(item => new SelectListItem(Role(text, item.Text), item.Value ?? item.Text, item.Selected));
}
