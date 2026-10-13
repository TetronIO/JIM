// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// How JIM compares two values when it asks whether they identify the same thing (#2051): ignoring case, unless the
/// attribute holding them is declared case-exact. One place for the rule, so every identity comparison agrees.
/// </summary>
public static class IdentifierKey
{
    /// <summary>
    /// The value as a key for identity lookups: unchanged when case-exact, else lower-cased with the invariant culture.
    /// Keys built this way are compared ordinally, so one dictionary can hold values of both kinds.
    /// </summary>
    public static string For(string value, bool caseExact) => caseExact ? value : value.ToLowerInvariant();

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
