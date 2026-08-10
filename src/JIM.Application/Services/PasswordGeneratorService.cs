// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using System.Security.Cryptography;
using System.Text;

namespace JIM.Application.Services;

/// <summary>
/// Generates initial passwords.
/// <para>
/// Every random choice made anywhere in this class goes through <see cref="RandomNumberGenerator"/>.
/// <see cref="Random"/> is never used and must never be: its output passes any statistical test a caller could
/// apply and is still reproducible by anyone who can guess when it ran, which for a password generated at a
/// predictable moment in a provisioning run is not a hypothetical.
/// </para>
/// <para>
/// The class holds no state and is safe to use from several threads at once.
/// </para>
/// </summary>
public class PasswordGeneratorService : IPasswordGeneratorService
{
    /// <inheritdoc />
    public string Generate(PasswordGenerationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Assessed against nothing rather than against a target: this guard is about whether the configuration
        // can be satisfied at all, which is JIM's own question. Whether the target would then accept the result
        // is the caller's to ask, and is not a reason to refuse to generate.
        var assessment = Assess(policy, null);
        if (!assessment.IsUsable)
            throw new ArgumentException(
                $"This password configuration cannot be satisfied: {string.Join(" ", assessment.Problems)}", nameof(policy));

        var pools = PasswordCharacterPools.For(policy);

        return policy.Style switch
        {
            PasswordGenerationStyle.RandomCharacters => GenerateRandomCharacters(policy, pools),
            PasswordGenerationStyle.Words => GenerateWords(policy, pools),
            PasswordGenerationStyle.Pronounceable => GeneratePronounceable(policy, pools),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy.Style, "Unhandled password generation style.")
        };
    }

    #region generation
    private static string GenerateRandomCharacters(PasswordGenerationPolicy policy, PasswordCharacterPools pools)
    {
        var characters = new List<char>(policy.Length);

        // The required categories first, so that satisfying them is arithmetic rather than luck. Their positions
        // come from the shuffle below; without it the first characters of every password would be predictable by
        // category, which is a far more useful clue to an attacker than it looks.
        AppendDraws(characters, pools.Uppercase, policy.MinimumUppercase);
        AppendDraws(characters, pools.Lowercase, policy.MinimumLowercase);
        AppendDraws(characters, pools.Digits, policy.MinimumDigits);
        AppendDraws(characters, pools.Symbols, policy.MinimumSymbols);

        AppendDraws(characters, pools.Everything, policy.Length - characters.Count);

        Shuffle(characters);
        return new string(characters.ToArray());
    }

    private static string GenerateWords(PasswordGenerationPolicy policy, PasswordCharacterPools pools)
    {
        var words = new string[policy.WordCount];
        for (var i = 0; i < words.Length; i++)
            words[i] = PasswordWordList.Words[RandomNumberGenerator.GetInt32(PasswordWordList.Words.Count)];

        ApplyCapitalisation(words, policy.WordCapitalisation);

        var password = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
                password.Append(SeparatorBetweenWords(policy, pools));

            password.Append(words[i]);
        }

        AppendTrailer(password, policy, pools, FixedSeparator(policy.WordSeparator));
        return password.ToString();
    }

    private static string GeneratePronounceable(PasswordGenerationPolicy policy, PasswordCharacterPools pools)
    {
        var password = new StringBuilder();
        for (var position = 0; position < policy.Length; position++)
        {
            // Broken into groups so a person can hold one at a time in their head while typing it, which is the
            // only reason this style exists.
            if (position > 0 && position % PronounceableGroupLength == 0)
                password.Append(PronounceableGroupSeparator);

            password.Append(Draw(position % 2 == 0 ? pools.Consonants : pools.Vowels));
        }

        AppendTrailer(password, policy, pools, PronounceableGroupSeparator);
        return password.ToString();
    }

    /// <summary>
    /// Appends the digits and symbol that the word and pronounceable styles use to reach the character
    /// categories their letters alone cannot.
    /// </summary>
    /// <param name="joiner">
    /// What to put in front of the appended digits, so they read as a distinct group rather than running into
    /// the last word. Null where the style has no separator to reuse.
    /// </param>
    private static void AppendTrailer(StringBuilder password, PasswordGenerationPolicy policy, PasswordCharacterPools pools, string? joiner)
    {
        if (policy.AppendedDigitCount > 0)
        {
            password.Append(joiner);
            for (var i = 0; i < policy.AppendedDigitCount; i++)
                password.Append(Draw(pools.Digits));
        }

        if (policy.AppendSymbol)
            password.Append(Draw(pools.Symbols));
    }

    private static void ApplyCapitalisation(string[] words, PasswordWordCapitalisation capitalisation)
    {
        switch (capitalisation)
        {
            case PasswordWordCapitalisation.Lowercase:
                return; // The list ships lowercase, so there is nothing to do.

            case PasswordWordCapitalisation.EachWord:
                for (var i = 0; i < words.Length; i++)
                    words[i] = Capitalise(words[i]);
                return;

            case PasswordWordCapitalisation.Uppercase:
                for (var i = 0; i < words.Length; i++)
                    words[i] = words[i].ToUpperInvariant();
                return;

            case PasswordWordCapitalisation.FirstWordOnly:
                words[0] = Capitalise(words[0]);
                return;

            case PasswordWordCapitalisation.RandomWord:
                var chosen = RandomNumberGenerator.GetInt32(words.Length);
                words[chosen] = Capitalise(words[chosen]);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(capitalisation), capitalisation, "Unhandled word capitalisation.");
        }
    }

    private static string Capitalise(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    private static string SeparatorBetweenWords(PasswordGenerationPolicy policy, PasswordCharacterPools pools) =>
        policy.WordSeparator switch
        {
            PasswordWordSeparator.Digit => Draw(pools.Digits).ToString(),
            PasswordWordSeparator.RandomSymbol => Draw(pools.Symbols).ToString(),
            _ => FixedSeparator(policy.WordSeparator) ?? string.Empty
        };

    /// <summary>
    /// The separator for the styles whose separator is the same every time, or null for the styles that draw a
    /// fresh one for each gap (and for no separator at all).
    /// </summary>
    private static string? FixedSeparator(PasswordWordSeparator separator) => separator switch
    {
        PasswordWordSeparator.Hyphen => "-",
        PasswordWordSeparator.FullStop => ".",
        PasswordWordSeparator.Underscore => "_",
        _ => null
    };
    #endregion

    #region randomness
    /// <summary>
    /// Draws one character.
    /// <para>
    /// <see cref="RandomNumberGenerator.GetInt32(int)"/> rather than a remainder of a random number: taking a
    /// remainder favours the start of the pool whenever the pool size does not divide the generator's range,
    /// and every pool here is an awkward size (25 letters, 8 digits, 12 symbols).
    /// </para>
    /// </summary>
    private static char Draw(string pool) => pool[RandomNumberGenerator.GetInt32(pool.Length)];

    private static void AppendDraws(List<char> characters, string pool, int count)
    {
        for (var i = 0; i < count; i++)
            characters.Add(Draw(pool));
    }

    /// <summary>
    /// Fisher-Yates, so that every ordering is equally likely.
    /// </summary>
    private static void Shuffle(List<char> characters)
    {
        for (var i = characters.Count - 1; i > 0; i--)
        {
            var swapWith = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[swapWith]) = (characters[swapWith], characters[i]);
        }
    }
    #endregion

    #region assessment
    /// <inheritdoc />
    public PasswordGenerationAssessment Assess(PasswordGenerationPolicy policy, ConnectedSystemPasswordPolicy? targetPolicy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var pools = PasswordCharacterPools.For(policy);
        var problems = new List<string>();

        var (guaranteedLength, guaranteedClasses, entropyBits) = policy.Style switch
        {
            PasswordGenerationStyle.RandomCharacters => AssessRandomCharacters(policy, pools, problems),
            PasswordGenerationStyle.Words => AssessWords(policy, pools, problems),
            PasswordGenerationStyle.Pronounceable => AssessPronounceable(policy, pools, problems),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy.Style, "Unhandled password generation style.")
        };

        CheckAgainstTarget(targetPolicy, guaranteedLength, guaranteedClasses, problems);

        return new PasswordGenerationAssessment
        {
            GuaranteedMinimumLength = guaranteedLength,
            GuaranteedCharacterClasses = guaranteedClasses,
            EntropyBits = entropyBits,
            Problems = problems
        };
    }

    private static (int Length, PasswordCharacterClasses Classes, double Entropy) AssessRandomCharacters(
        PasswordGenerationPolicy policy, PasswordCharacterPools pools, List<string> problems)
    {
        CheckLength(policy.Length, problems);

        var minimums = new[]
        {
            (Name: "uppercase letters", Count: policy.MinimumUppercase, Pool: pools.Uppercase, Class: PasswordCharacterClasses.Uppercase),
            (Name: "lowercase letters", Count: policy.MinimumLowercase, Pool: pools.Lowercase, Class: PasswordCharacterClasses.Lowercase),
            (Name: "digits", Count: policy.MinimumDigits, Pool: pools.Digits, Class: PasswordCharacterClasses.Digit),
            (Name: "symbols", Count: policy.MinimumSymbols, Pool: pools.Symbols, Class: PasswordCharacterClasses.Symbol)
        };

        foreach (var minimum in minimums.Where(m => m.Count < 0))
            problems.Add($"The minimum number of {minimum.Name} cannot be negative.");

        var required = minimums.Sum(m => Math.Max(0, m.Count));
        if (required > policy.Length)
            problems.Add($"The per-category minimums add up to {required} characters, which is more than the password length of {policy.Length}.");

        foreach (var minimum in minimums.Where(m => m.Count > 0 && m.Pool.Length == 0))
            problems.Add($"At least {minimum.Count} {minimum.Name} are required, but none are permitted. Widen the permitted characters or lower the minimum.");

        if (pools.Everything.Length == 0)
            problems.Add("No characters are permitted at all, so no password can be generated.");

        var classes = PasswordCharacterClasses.None;
        var entropy = 0d;
        foreach (var minimum in minimums.Where(m => m.Count > 0 && m.Pool.Length > 0))
        {
            classes |= minimum.Class;
            entropy += minimum.Count * Math.Log2(minimum.Pool.Length);
        }

        // Deliberately excludes what the shuffle contributes. The free positions are counted at the full pool
        // size and the ordering is treated as worth nothing, which puts the figure below the truth rather than
        // above it.
        var free = Math.Max(0, policy.Length - required);
        if (pools.Everything.Length > 0)
            entropy += free * Math.Log2(pools.Everything.Length);

        return (policy.Length, classes, entropy);
    }

    private static (int Length, PasswordCharacterClasses Classes, double Entropy) AssessWords(
        PasswordGenerationPolicy policy, PasswordCharacterPools pools, List<string> problems)
    {
        if (policy.WordCount < 1)
            problems.Add("A passphrase needs at least one word.");

        if (policy.WordCount > MaximumWordCount)
            problems.Add($"A passphrase of more than {MaximumWordCount} words is longer than anyone will transcribe.");

        CheckAppendedCharacters(policy, pools, problems);

        var gaps = Math.Max(0, policy.WordCount - 1);
        var fixedSeparator = FixedSeparator(policy.WordSeparator);

        if (gaps > 0 && policy.WordSeparator == PasswordWordSeparator.RandomSymbol && pools.Symbols.Length == 0)
            problems.Add("The words are to be separated by a symbol, but no symbols are permitted.");

        var classes = PasswordCharacterClasses.None;

        // Every word is at least four letters, so capitalising a word's first letter always leaves lowercase
        // letters behind it. Read from the list rather than assumed, since the list is what can change.
        if (policy.WordCapitalisation != PasswordWordCapitalisation.Uppercase &&
            (policy.WordCapitalisation == PasswordWordCapitalisation.Lowercase || PasswordWordList.ShortestWordLength >= 2))
            classes |= PasswordCharacterClasses.Lowercase;

        if (policy.WordCapitalisation != PasswordWordCapitalisation.Lowercase && policy.WordCount >= 1)
            classes |= PasswordCharacterClasses.Uppercase;

        if (policy.AppendedDigitCount > 0 || (gaps > 0 && policy.WordSeparator == PasswordWordSeparator.Digit))
            classes |= PasswordCharacterClasses.Digit;

        var separatorIsSymbol = fixedSeparator != null || policy.WordSeparator == PasswordWordSeparator.RandomSymbol;
        if ((gaps > 0 && separatorIsSymbol) ||
            (policy.AppendedDigitCount > 0 && fixedSeparator != null) ||
            (policy.AppendSymbol && pools.Symbols.Length > 0))
            classes |= PasswordCharacterClasses.Symbol;

        var length =
            policy.WordCount * PasswordWordList.ShortestWordLength +
            gaps * (policy.WordSeparator == PasswordWordSeparator.None ? 0 : 1) +
            TrailerLength(policy, fixedSeparator);

        var entropy = policy.WordCount * Math.Log2(PasswordWordList.Words.Count);

        if (policy.WordCapitalisation == PasswordWordCapitalisation.RandomWord && policy.WordCount > 1)
            entropy += Math.Log2(policy.WordCount);

        if (gaps > 0 && policy.WordSeparator == PasswordWordSeparator.Digit)
            entropy += gaps * Log2OrZero(pools.Digits.Length);

        if (gaps > 0 && policy.WordSeparator == PasswordWordSeparator.RandomSymbol)
            entropy += gaps * Log2OrZero(pools.Symbols.Length);

        return (length, classes, entropy + TrailerEntropy(policy, pools));
    }

    private static (int Length, PasswordCharacterClasses Classes, double Entropy) AssessPronounceable(
        PasswordGenerationPolicy policy, PasswordCharacterPools pools, List<string> problems)
    {
        CheckLength(policy.Length, problems);
        CheckAppendedCharacters(policy, pools, problems);

        if (pools.Consonants.Length == 0 || pools.Vowels.Length == 0)
            problems.Add("Too many characters have been excluded to build a pronounceable password.");

        var groupSeparators = policy.Length > PronounceableGroupLength ? (policy.Length - 1) / PronounceableGroupLength : 0;

        // Always lowercase letters, and the grouping hyphens count as symbols to anything examining character
        // categories, which is why a short pronounceable password guarantees fewer categories than a long one.
        var classes = PasswordCharacterClasses.Lowercase;
        if (policy.AppendedDigitCount > 0)
            classes |= PasswordCharacterClasses.Digit;
        if (groupSeparators > 0 || policy.AppendedDigitCount > 0 || (policy.AppendSymbol && pools.Symbols.Length > 0))
            classes |= PasswordCharacterClasses.Symbol;

        var consonants = (policy.Length + 1) / 2;
        var vowels = policy.Length / 2;
        var entropy = consonants * Log2OrZero(pools.Consonants.Length) + vowels * Log2OrZero(pools.Vowels.Length);

        var length = policy.Length + groupSeparators + TrailerLength(policy, PronounceableGroupSeparator);
        return (length, classes, entropy + TrailerEntropy(policy, pools));
    }

    private static void CheckLength(int length, List<string> problems)
    {
        if (length < MinimumLength)
            problems.Add($"A password of fewer than {MinimumLength} characters is not worth generating.");

        if (length > MaximumLength)
            problems.Add($"A password of more than {MaximumLength} characters is longer than systems generally accept.");
    }

    private static void CheckAppendedCharacters(PasswordGenerationPolicy policy, PasswordCharacterPools pools, List<string> problems)
    {
        if (policy.AppendedDigitCount < 0)
            problems.Add("The number of digits to append cannot be negative.");

        if (policy.AppendSymbol && pools.Symbols.Length == 0)
            problems.Add("A symbol is to be appended, but no symbols are permitted.");

        if (policy.AppendedDigitCount > 0 && pools.Digits.Length == 0)
            problems.Add("Digits are to be appended, but no digits are permitted.");
    }

    private static int TrailerLength(PasswordGenerationPolicy policy, string? joiner) =>
        (policy.AppendedDigitCount > 0 ? policy.AppendedDigitCount + (joiner?.Length ?? 0) : 0) +
        (policy.AppendSymbol ? 1 : 0);

    private static double TrailerEntropy(PasswordGenerationPolicy policy, PasswordCharacterPools pools) =>
        Math.Max(0, policy.AppendedDigitCount) * Log2OrZero(pools.Digits.Length) +
        (policy.AppendSymbol ? Log2OrZero(pools.Symbols.Length) : 0);

    /// <summary>
    /// The bits a draw from a pool of this size is worth. An empty pool is worth nothing rather than negative
    /// infinity, which is what <c>Log2(0)</c> would otherwise contribute to the total.
    /// </summary>
    private static double Log2OrZero(int poolSize) => poolSize > 0 ? Math.Log2(poolSize) : 0d;

    /// <inheritdoc />
    public SuppliedPasswordAssessment AssessSupplied(string? password, ConnectedSystemPasswordPolicy? targetPolicy)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(password))
        {
            // Reported rather than thrown: the portal asks this question on every keystroke, and an empty field
            // is the state it starts in. Nothing else is worth checking about a password that does not exist.
            problems.Add("Enter a password.");
            return new SuppliedPasswordAssessment
            {
                Length = 0,
                CharacterClasses = PasswordCharacterClasses.None,
                Problems = problems
            };
        }

        var classes = ClassesIn(password);

        if (password.Length < MinimumLength)
            problems.Add($"A password of fewer than {MinimumLength} characters is not worth setting.");

        if (password.Length > MaximumLength)
            problems.Add($"A password of more than {MaximumLength} characters is longer than systems generally accept.");

        if (targetPolicy?.MinimumLength is { } minimumLength && password.Length < minimumLength)
            problems.Add($"This Connected System requires at least {minimumLength} characters, and this password has {password.Length}.");

        if (targetPolicy is { ComplexityRequired: true, RequiredCharacterClassCount: { } requiredClasses })
        {
            var satisfied = System.Numerics.BitOperations.PopCount((uint)CountedClasses(targetPolicy, classes));
            if (satisfied < requiredClasses)
                problems.Add($"This Connected System requires characters from at least {requiredClasses} categories, and this password has {satisfied}. Adding a digit or a symbol is the usual way to close the gap.");
        }

        return new SuppliedPasswordAssessment
        {
            Length = password.Length,
            CharacterClasses = classes,
            Problems = problems
        };
    }

    /// <summary>
    /// The categories present in a password, in the terms a target counts them.
    /// <para>
    /// Anything that is neither a letter nor a base 10 digit is a symbol, which is how systems expressing
    /// complexity as "non-alphanumeric" read it. Letters with no case of their own are their own category rather
    /// than being folded into one of the cased ones, matching how Active Directory counts them.
    /// </para>
    /// </summary>
    private static PasswordCharacterClasses ClassesIn(string password)
    {
        var classes = PasswordCharacterClasses.None;

        foreach (var character in password)
        {
            if (char.IsUpper(character))
                classes |= PasswordCharacterClasses.Uppercase;
            else if (char.IsLower(character))
                classes |= PasswordCharacterClasses.Lowercase;
            else if (char.IsAsciiDigit(character))
                classes |= PasswordCharacterClasses.Digit;
            else if (char.IsLetter(character))
                classes |= PasswordCharacterClasses.OtherUnicodeLetter;
            else
                classes |= PasswordCharacterClasses.Symbol;
        }

        return classes;
    }

    /// <summary>
    /// The categories that count towards a target's complexity rule.
    /// <para>
    /// Where JIM did not discover which categories a system counts, every category is counted rather than none:
    /// refusing a password on the strength of something undiscovered would be reading a silence as a denial.
    /// </para>
    /// </summary>
    private static PasswordCharacterClasses CountedClasses(ConnectedSystemPasswordPolicy targetPolicy, PasswordCharacterClasses classes) =>
        targetPolicy.RecognisedCharacterClasses == PasswordCharacterClasses.None
            ? classes
            : classes & targetPolicy.RecognisedCharacterClasses;

    private static void CheckAgainstTarget(
        ConnectedSystemPasswordPolicy? targetPolicy,
        int guaranteedLength,
        PasswordCharacterClasses guaranteedClasses,
        List<string> problems)
    {
        if (targetPolicy == null)
            return;

        if (targetPolicy.MinimumLength is { } minimumLength && guaranteedLength < minimumLength)
            problems.Add($"This Connected System requires at least {minimumLength} characters, and this configuration can produce as few as {guaranteedLength}.");

        if (targetPolicy.ComplexityRequired != true || targetPolicy.RequiredCharacterClassCount is not { } requiredClasses)
            return;

        // Only categories the target actually counts are worth anything; see CountedClasses for why an
        // undiscovered set counts everything rather than nothing.
        var satisfied = System.Numerics.BitOperations.PopCount((uint)CountedClasses(targetPolicy, guaranteedClasses));
        if (satisfied < requiredClasses)
            problems.Add($"This Connected System requires characters from at least {requiredClasses} categories, and this configuration guarantees only {satisfied}. Appending digits or a symbol is the usual way to close the gap.");
    }
    #endregion

    /// <inheritdoc />
    public PasswordGenerationPolicy DeriveFrom(ConnectedSystemPasswordPolicy? targetPolicy)
    {
        // The defaults already guarantee all four character categories JIM can produce, which satisfies every
        // complexity rule expressible as "at least N categories" that JIM could meet at all. Length is the one
        // thing a target can demand more of.
        var policy = new PasswordGenerationPolicy();

        if (targetPolicy?.MinimumLength is { } minimumLength && minimumLength > policy.Length)
            policy.Length = Math.Min(minimumLength, MaximumLength);

        return policy;
    }

    /// <inheritdoc />
    public PasswordPolicyReconciliation Reconcile(IReadOnlyList<PasswordPolicyForSystem> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var known = policies.Where(p => p.Policy is { HasAnyDiscoveredConstraint: true }).ToList();
        var combined = Combine(known.Select(p => p.Policy!).ToList());
        var derived = DeriveFrom(combined);

        // Checked against each system in its own right, not against the combination. The combination is a
        // construct; what will actually refuse a password is a system, and this is the shape that reports
        // which one. It cannot fail today (the settings are derived from these very constraints, and JIM
        // discovers no maximum length to contradict a minimum), which is exactly why it is worth having: the
        // day a conflicting constraint is discoverable, this reports it before a password is generated.
        var conflicts = known
            .Select(system => new { system.ConnectedSystemName, Assessment = Assess(derived, system.Policy) })
            .Where(checkedSystem => !checkedSystem.Assessment.IsUsable)
            .Select(checkedSystem => $"{checkedSystem.ConnectedSystemName}: {string.Join(" ", checkedSystem.Assessment.Problems)}")
            .ToList();

        return new PasswordPolicyReconciliation
        {
            Policy = derived,
            Constraints = DescribeConstraints(combined),
            SystemsWithNoDiscoveredPolicy = policies
                .Where(p => p.Policy is not { HasAnyDiscoveredConstraint: true })
                .Select(p => p.ConnectedSystemName)
                .ToList(),
            Conflicts = conflicts,
            // Any system that may hold a stricter policy for some accounts, or that JIM could not ask, makes the
            // whole combination a floor rather than a guarantee.
            MayBeStricterThanDiscovered = known.Any(p => p.Policy!.FineGrainedPolicySignal != FineGrainedPolicySignal.Absent)
        };
    }

    /// <summary>
    /// Folds several discovered policies into the single strictest one.
    /// <para>
    /// Lengths and category counts take the maximum, because satisfying the strictest satisfies the rest. The
    /// recognised categories take the <b>intersection</b>, which is the part that is easy to get backwards: a
    /// category only one system counts is worthless for satisfying another system's "at least N categories",
    /// so counting the union would promise a compliance the password does not have.
    /// </para>
    /// </summary>
    private static ConnectedSystemPasswordPolicy? Combine(IReadOnlyList<ConnectedSystemPasswordPolicy> policies)
    {
        if (policies.Count == 0)
            return null;

        var recognised = policies
            .Where(p => p.RecognisedCharacterClasses != PasswordCharacterClasses.None)
            .Select(p => p.RecognisedCharacterClasses)
            .ToList();

        return new ConnectedSystemPasswordPolicy
        {
            MinimumLength = policies.Select(p => p.MinimumLength).Max(),
            ComplexityRequired = policies.Any(p => p.ComplexityRequired == true) ? true : null,
            RequiredCharacterClassCount = policies.Select(p => p.RequiredCharacterClassCount).Max(),
            // None where no system said which categories it counts, which downstream reads as "count them all"
            // rather than "count none"; reading a silence as a denial is the mistake this avoids.
            RecognisedCharacterClasses = recognised.Count == 0
                ? PasswordCharacterClasses.None
                : recognised.Aggregate((left, right) => left & right),
            PasswordHistoryLength = policies.Select(p => p.PasswordHistoryLength).Max(),
            MaximumPasswordAge = policies.Select(p => p.MaximumPasswordAge).Min(),
            MinimumPasswordAge = policies.Select(p => p.MinimumPasswordAge).Max(),
            FineGrainedPolicySignal = policies.Any(p => p.FineGrainedPolicySignal == FineGrainedPolicySignal.Present)
                ? FineGrainedPolicySignal.Present
                : policies.Any(p => p.FineGrainedPolicySignal == FineGrainedPolicySignal.CouldNotDetermine)
                    ? FineGrainedPolicySignal.CouldNotDetermine
                    : FineGrainedPolicySignal.Absent
        };
    }

    /// <summary>
    /// Says what the combined rules amount to, for showing beside the Generate button. Only the constraints that
    /// bear on what is generated: a password history length explains a rejection but does not shape a password.
    /// </summary>
    private static List<string> DescribeConstraints(ConnectedSystemPasswordPolicy? combined)
    {
        var constraints = new List<string>();
        if (combined == null)
            return constraints;

        if (combined.MinimumLength is { } minimumLength)
            constraints.Add($"{minimumLength} characters or more");

        if (combined is { ComplexityRequired: true, RequiredCharacterClassCount: { } requiredClasses })
        {
            var recognised = combined.RecognisedCharacterClasses == PasswordCharacterClasses.None
                ? 4
                : System.Numerics.BitOperations.PopCount((uint)combined.RecognisedCharacterClasses);
            constraints.Add($"{requiredClasses} of {recognised} character categories");
        }

        return constraints;
    }

    #region constants
    /// <summary>
    /// How many letters of a pronounceable password go between one grouping separator and the next.
    /// <para>
    /// Even on purpose. Letters alternate consonant and vowel by absolute position, so an odd group length would
    /// leave every other group starting on a vowel, which is what makes a group awkward to say. The cost is that
    /// the last group can be short.
    /// </para>
    /// </summary>
    private const int PronounceableGroupLength = 6;

    private const string PronounceableGroupSeparator = "-";

    private const int MinimumLength = 4;

    /// <summary>
    /// Longer than Active Directory's own limit, so JIM's ceiling is never the binding one in practice.
    /// </summary>
    private const int MaximumLength = 256;

    private const int MaximumWordCount = 16;
    #endregion
}
