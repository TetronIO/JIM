// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// The characters a <see cref="PasswordGenerationPolicy"/> permits, worked out once so that generation and
/// assessment cannot disagree about them.
/// <para>
/// That sharing is the point. The entropy an administrator is shown is calculated from the size of these sets,
/// and the password is built by drawing from the very same sets; if the two were derived separately, an
/// exclusion applied in one and forgotten in the other would overstate the strength of every password JIM
/// generated, silently.
/// </para>
/// </summary>
internal sealed class PasswordCharacterPools
{
    private PasswordCharacterPools(string uppercase, string lowercase, string digits, string symbols, string consonants, string vowels)
    {
        Uppercase = uppercase;
        Lowercase = lowercase;
        Digits = digits;
        Symbols = symbols;
        Consonants = consonants;
        Vowels = vowels;
        Everything = uppercase + lowercase + digits + symbols;
    }

    internal string Uppercase { get; }

    internal string Lowercase { get; }

    internal string Digits { get; }

    /// <summary>
    /// The permitted symbols, deduplicated. Can be empty, which is a configuration JIM has to cope with rather
    /// than assume away.
    /// </summary>
    internal string Symbols { get; }

    /// <summary>
    /// Every permitted character, which is what the free positions of a random-character password are drawn from.
    /// </summary>
    internal string Everything { get; }

    internal string Consonants { get; }

    internal string Vowels { get; }

    internal static PasswordCharacterPools For(PasswordGenerationPolicy policy)
    {
        var excluded = policy.ExcludeAmbiguousCharacters ? PasswordGenerationPolicy.AmbiguousCharacters : string.Empty;

        return new PasswordCharacterPools(
            Filter(AllUppercase, excluded),
            Filter(AllLowercase, excluded),
            Filter(AllDigits, excluded),
            Filter(Deduplicate(policy.PermittedSymbols), excluded),
            Filter(PronounceableConsonants, excluded),
            Filter(PronounceableVowels, excluded));
    }

    private static string Filter(string source, string excluded) =>
        excluded.Length == 0 ? source : new string(source.Where(c => !excluded.Contains(c)).ToArray());

    /// <summary>
    /// Removes repeats from an administrator-supplied symbol set, since a character listed twice would otherwise
    /// be twice as likely to be drawn and would inflate the entropy figure.
    /// </summary>
    private static string Deduplicate(string symbols) => new(symbols.Distinct().ToArray());

    private const string AllUppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string AllLowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string AllDigits = "0123456789";

    /// <summary>
    /// The consonants the pronounceable style draws on. Deliberately not the whole alphabet: <c>q</c> is
    /// unsayable without a following <c>u</c>, and <c>x</c> reads badly at the start of a syllable.
    /// </summary>
    private const string PronounceableConsonants = "bcdfghjklmnprstvwyz";

    private const string PronounceableVowels = "aeiou";
}
