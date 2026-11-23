// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using ModelPluralRules = JIM.Models.Localisation.PluralRules;

namespace JIM.Web.Localisation;

/// <summary>
/// Picks the singular or plural form of a counted phrase by the rules of the viewer's language (#3030). The rules live
/// in <see cref="ModelPluralRules"/>, which the message renderer shares; this keeps the portal's own call sites short.
/// </summary>
public static class PluralRules
{
    /// <summary>The form of <paramref name="count"/> in the current UI culture.</summary>
    public static string Choose(long count, string one, string other) => ModelPluralRules.Choose(count, one, other);

    /// <summary>The form of <paramref name="count"/> in <paramref name="culture"/>.</summary>
    public static string Choose(CultureInfo culture, long count, string one, string other) =>
        ModelPluralRules.Choose(culture, count, one, other);
}
