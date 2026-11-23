// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace JIM.Web.Localisation;

/// <summary>
/// The portal's request localisation (#3030): which culture each request runs in, and how a user's choice is stored.
/// </summary>
public static class PortalLocalisation
{
    /// <summary>
    /// Request localisation for the portal: the culture cookie first, then the browser's <c>Accept-Language</c>, then
    /// the default. The query-string provider ASP.NET Core adds by default is left out, so a link cannot switch someone's
    /// language. A requested culture with no language of its own falls back to its parent (de-AT to de) before the
    /// default.
    /// </summary>
    /// <param name="includeUnreleased">Whether unreleased languages are offered (development builds).</param>
    /// <param name="serverCulture">
    /// The culture the host runs in, from <c>LANG</c>: a documented setting that decides how the portal writes dates and
    /// numbers. Someone who has not chosen a language keeps those formats, with British English text; only choosing a
    /// language changes both, so a deployment's formats are exactly what they were before languages existed, even with
    /// no <c>LANG</c> at all (the invariant culture).
    /// </param>
    public static RequestLocalizationOptions CreateOptions(bool includeUnreleased, CultureInfo serverCulture)
    {
        var uiCultures = UiLanguages.OfferedCultures(includeUnreleased);
        var formattingCultures = uiCultures.ToList();
        if (!formattingCultures.Any(culture => culture.Name == serverCulture.Name))
            formattingCultures.Add(serverCulture);

        return new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(serverCulture, CultureInfo.GetCultureInfo(UiLanguages.Default.CultureName)),
            SupportedCultures = formattingCultures,
            SupportedUICultures = uiCultures,
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
