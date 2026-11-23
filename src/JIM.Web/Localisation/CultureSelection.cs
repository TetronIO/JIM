// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Localization;

namespace JIM.Web.Localisation;

/// <summary>
/// Turns a language chosen in the portal into the culture cookie request localisation reads (#3030), and decides
/// where to send the browser afterwards. Both inputs arrive on a query string, so neither is trusted.
/// </summary>
public static class CultureSelection
{
    /// <summary>The route the language picker sends the browser to.</summary>
    public const string Path = "/culture/set";

    /// <summary>How long the choice is remembered in the browser.</summary>
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

    /// <summary>
    /// The culture cookie's value for an offered language, or null when <paramref name="cultureName"/> is not one.
    /// The formatting culture and the text culture are the same: a user who chooses Deutsch gets German dates and
    /// numbers as well as German words.
    /// </summary>
    public static string? CookieValueFor(string? cultureName, bool includeUnreleased)
    {
        var language = UiLanguages.Find(cultureName, includeUnreleased);
        if (language == null)
            return null;

        return CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(language.CultureName));
    }

    /// <summary>
    /// Where to send the browser once the cookie is set: the page the user was on, provided it is a path on this
    /// site, otherwise the home page. A protocol-relative (<c>//host</c>) or backslash (<c>/\host</c>) path is
    /// refused, since browsers treat both as another site.
    /// </summary>
    public static string SafeReturnPath(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/')
            return "/";

        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
            return "/";

        return returnUrl.Any(char.IsControl) ? "/" : returnUrl;
    }

    /// <summary>
    /// The address the picker navigates to for a language, returning to <paramref name="currentPath"/>.
    /// </summary>
    public static string SelectionUrl(string cultureName, string currentPath) =>
        $"{Path}?culture={Uri.EscapeDataString(cultureName)}&returnUrl={Uri.EscapeDataString(currentPath)}";
}
