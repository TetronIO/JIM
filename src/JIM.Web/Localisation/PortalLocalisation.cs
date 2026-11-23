// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Localization;

namespace JIM.Web.Localisation;

/// <summary>
/// The portal's request localisation (#3030): which culture each request runs in, and how a user's choice is stored.
/// </summary>
public static class PortalLocalisation
{
    /// <summary>
    /// Request localisation for the portal: the culture cookie first, then the browser's <c>Accept-Language</c>, then
    /// en-GB. The query-string provider ASP.NET Core adds by default is left out, so a link cannot switch someone's
    /// language. A requested culture with no language of its own falls back to its parent (de-AT to de) before the
    /// default.
    /// </summary>
    public static RequestLocalizationOptions CreateOptions(bool includeUnreleased)
    {
        var cultures = UiLanguages.OfferedCultures(includeUnreleased);
        return new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(UiLanguages.Default.CultureName),
            SupportedCultures = cultures,
            SupportedUICultures = cultures,
            FallBackToParentCultures = true,
            FallBackToParentUICultures = true,
            RequestCultureProviders =
            [
                new CookieRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider()
            ]
        };
    }

    /// <summary>
    /// Stores a language chosen in the picker and sends the browser back where it came from. An unknown culture
    /// changes nothing; it is not an error worth showing anyone, since only a hand-built address can produce one.
    /// </summary>
    public static IResult ApplySelection(HttpContext context, string? culture, string? returnUrl, bool includeUnreleased)
    {
        var cookieValue = CultureSelection.CookieValueFor(culture, includeUnreleased);
        if (cookieValue != null)
        {
            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                cookieValue,
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.Add(CultureSelection.CookieLifetime),
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    IsEssential = true,
                    Path = "/"
                });
        }

        return Results.LocalRedirect(CultureSelection.SafeReturnPath(returnUrl));
    }
}
