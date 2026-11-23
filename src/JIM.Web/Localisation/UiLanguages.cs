// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Web.Localisation;

/// <summary>
/// The languages the portal can be shown in (#3030), and which of them a given environment offers.
/// </summary>
/// <remarks>
/// Adding a language is a resource-file change plus one entry here; see <c>engineering/LOCALISATION.md</c>. The
/// neutral resources are en-GB, so a culture with no file of its own (en-AU, en-IE, en-IN) falls back to British
/// English, which is correct for it.
/// </remarks>
public static class UiLanguages
{
    /// <summary>The language the portal falls back to: the neutral (en-GB) resources.</summary>
    public static readonly UiLanguage Default = new("en-GB", "English (UK)", LanguageStage.Released);

    /// <summary>Every language the portal has resources for, in the order the picker lists them.</summary>
    public static readonly IReadOnlyList<UiLanguage> All =
    [
        Default,
        new("en-US", "English (US)", LanguageStage.Released),
        new("fr", "Français", LanguageStage.Released),
        new("de", "Deutsch", LanguageStage.Released)
    ];

    /// <summary>
    /// The languages a user can choose in this environment: the released ones, plus the unreleased ones in a
    /// development build.
    /// </summary>
    public static IReadOnlyList<UiLanguage> Offered(bool includeUnreleased) =>
        All.Where(language => includeUnreleased || language.IsReleased).ToList();

    /// <summary>
    /// The cultures request localisation accepts, in the order it prefers them.
    /// </summary>
    public static IList<CultureInfo> OfferedCultures(bool includeUnreleased) =>
        Offered(includeUnreleased).Select(language => CultureInfo.GetCultureInfo(language.CultureName)).ToList();

    /// <summary>
    /// The offered language whose culture is named exactly <paramref name="cultureName"/> (ignoring case), or null.
    /// Nothing else is accepted: a culture name arriving from a request is never trusted further than this list.
    /// </summary>
    public static UiLanguage? Find(string? cultureName, bool includeUnreleased)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            return null;

        return Offered(includeUnreleased)
            .FirstOrDefault(language => string.Equals(language.CultureName, cultureName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The offered language a culture is served from: the language itself, or the nearest parent culture that is one
    /// (de-AT is served from de), else <see cref="Default"/>. This is what the picker shows as the current choice.
    /// </summary>
    public static UiLanguage Current(CultureInfo uiCulture, bool includeUnreleased)
    {
        for (var culture = uiCulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            var match = Find(culture.Name, includeUnreleased);
            if (match != null)
                return match;
        }

        return Default;
    }
}
