// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Web.Localisation;

/// <summary>
/// Picks the singular or plural form of a counted phrase by the rules of the viewer's language (#3030). Never build
/// a plural by appending "s": "1 Tag" / "2 Tagen", "0 jour" / "2 jours" are not English rules.
/// </summary>
/// <remarks>
/// Every language the portal ships needs only two forms (CLDR's "one" and "other"). A language with more (Polish,
/// Arabic) would add its categories here and a resource per category; see <c>engineering/LOCALISATION.md</c>.
/// </remarks>
public static class PluralRules
{
    /// <summary>The form of <paramref name="count"/> in the current UI culture.</summary>
    public static string Choose(long count, string one, string other) =>
        Choose(CultureInfo.CurrentUICulture, count, one, other);

    /// <summary>The form of <paramref name="count"/> in <paramref name="culture"/>.</summary>
    public static string Choose(CultureInfo culture, long count, string one, string other) =>
        IsOne(culture, count) ? one : other;

    private static bool IsOne(CultureInfo culture, long count) => culture.TwoLetterISOLanguageName switch
    {
        // French treats zero as singular: "0 jour", "1 jour", "2 jours".
        "fr" => count is 0 or 1,
        _ => count == 1
    };
}
