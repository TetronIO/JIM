// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// How JIM compares two values when it asks whether they identify the same thing (#2051): ignoring case, unless the
/// attribute holding them is declared case-exact. One place for the rule, so every identity comparison agrees.
/// </summary>
public static class IdentifierKey
{
    private const char TurkishCapitalDottedI = '\u0130';

    /// <summary>
    /// The value as a key for identity lookups: unchanged when case-exact, else lower-cased with the invariant culture.
    /// Keys built this way are compared ordinally, so one dictionary can hold values of both kinds.
    /// </summary>
    /// <remarks>
    /// Lower-casing agrees with PostgreSQL's <c>lower()</c>, because some lookups fold the stored value in SQL and the
    /// value sought in .NET. The two differ on one character in practice: the Turkish capital dotted I (U+0130), which
    /// .NET's invariant lowering leaves alone and PostgreSQL lowers to <c>i</c>, so it is lowered here too. (A sweep of
    /// every Basic Multilingual Plane character found the only other differences in letters added in Unicode 16.)
    /// </remarks>
    public static string For(string value, bool caseExact)
    {
        if (caseExact)
            return value;

        var lowered = value.ToLowerInvariant();
        return lowered.Contains(TurkishCapitalDottedI) ? lowered.Replace(TurkishCapitalDottedI, 'i') : lowered;
    }

    /// <summary>
    /// The string comparison an identity comparison of values of this kind uses.
    /// </summary>
    public static StringComparison Comparison(bool caseExact) =>
        caseExact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The string comparer an identity comparison of values of this kind uses, for sets and dictionaries.
    /// </summary>
    public static StringComparer Comparer(bool caseExact) =>
        caseExact ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
