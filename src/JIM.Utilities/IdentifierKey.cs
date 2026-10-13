// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// How JIM compares two values when it asks whether they identify the same thing (#2051): ignoring case, unless the
/// attribute holding them is declared case-exact. One place for the rule, so every identity comparison agrees: a key
/// built by <see cref="For"/>, a comparison by <see cref="AreSame"/> and a set or dictionary using <see cref="Comparer"/>
/// all give the same answer for the same two values.
/// </summary>
/// <remarks>
/// <para>
/// Ignoring case means folding each character with <see cref="char.ToLowerInvariant"/>, except the Turkish capital
/// dotted I (U+0130), which invariant lowering leaves alone and which is folded to <c>i</c> here, as Unicode's simple
/// case mapping does. Folding character by character keeps a value's length and lets the comparer work without building
/// strings; characters outside the Basic Multilingual Plane are left as they are.
/// </para>
/// <para>
/// The rule applies in .NET. Database lookups fold both sides in SQL (<c>lower(column) = lower(@value)</c>) and never
/// compare a .NET-folded value with an SQL-folded one, because the two need not agree: PostgreSQL's <c>lower()</c>
/// follows the database's character type, and a C or POSIX one folds ASCII only.
/// </para>
/// </remarks>
public static class IdentifierKey
{
    private const char TurkishCapitalDottedI = 'İ';

    private static readonly StringComparer IgnoringCaseComparer = new FoldingComparer();

    /// <summary>
    /// The value as a key for identity lookups: unchanged when case-exact, else folded (see the remarks). Keys built
    /// this way are compared ordinally, so one dictionary can hold values of both kinds.
    /// </summary>
    public static string For(string value, bool caseExact)
    {
        if (caseExact)
            return value;

        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                destination[i] = Fold(source[i]);
        });
    }

    /// <summary>
    /// Whether two values identify the same thing. Two nulls are the same; a null and a value are not.
    /// </summary>
    public static bool AreSame(string? first, string? second, bool caseExact) => Comparer(caseExact).Equals(first, second);

    /// <summary>
    /// The string comparer an identity comparison of values of this kind uses, for sets and dictionaries.
    /// </summary>
    public static StringComparer Comparer(bool caseExact) => caseExact ? StringComparer.Ordinal : IgnoringCaseComparer;

    private static char Fold(char character) =>
        character == TurkishCapitalDottedI ? 'i' : char.ToLowerInvariant(character);

    /// <summary>
    /// Compares by the fold <see cref="For"/> applies, without building the folded strings.
    /// </summary>
    private sealed class FoldingComparer : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var length = Math.Min(x.Length, y.Length);
            for (var i = 0; i < length; i++)
            {
                var difference = Fold(x[i]).CompareTo(Fold(y[i]));
                if (difference != 0)
                    return difference;
            }

            return x.Length.CompareTo(y.Length);
        }

        public override bool Equals(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null || x.Length != y.Length) return false;

            for (var i = 0; i < x.Length; i++)
            {
                if (Fold(x[i]) != Fold(y[i]))
                    return false;
            }

            return true;
        }

        public override int GetHashCode(string obj)
        {
            ArgumentNullException.ThrowIfNull(obj);

            var hash = new HashCode();
            foreach (var character in obj)
                hash.Add(Fold(character));
            return hash.ToHashCode();
        }
    }
}
