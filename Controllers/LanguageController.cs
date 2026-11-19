using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Localization;

namespace Wayfarer.Controllers;

/// <summary>Persists the browser's Web presentation preference.</summary>
[AllowAnonymous]
public sealed class LanguageController(WebLocales locales) : Controller
{
    /// <summary>Accepts a canonical locale from an antiforgery-protected form.</summary>
    [HttpPost("/Language/Set")]
    [ValidateAntiForgeryToken]
    public IActionResult Set([FromForm] string? locale)
    {
        if (locale is null || locales.Find(locale)?.Tag != locale)
            return BadRequest();

        Response.Cookies.Append(WebLocales.CookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(locale)),
            new CookieOptions
            {
                HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax,
                Path = "/", MaxAge = TimeSpan.FromDays(365), IsEssential = true
            });
        return NoContent();
    }
}
