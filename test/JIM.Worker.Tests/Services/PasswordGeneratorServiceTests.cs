// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Covers the password generator.
/// <para>
/// A generator is a bad fit for example-based testing: any single generated value proves nothing, because the
/// next one is different. Almost every test here therefore asserts an invariant over many generated values,
/// which is what "compliant by construction" actually has to mean to be worth claiming.
/// </para>
/// </summary>
[TestFixture]
public class PasswordGeneratorServiceTests
{
    private PasswordGeneratorService _generator = null!;

    /// <summary>
    /// Enough passwords that a one-in-a-hundred construction fault shows up reliably, and few enough to keep the
    /// suite quick.
    /// </summary>
    private const int Iterations = 500;

    [SetUp]
    public void SetUp()
    {
        _generator = new PasswordGeneratorService();
    }

    #region random characters
    [Test]
    public void Generate_RandomCharacters_ProducesTheRequestedLength()
    {
        var policy = new PasswordGenerationPolicy { Style = PasswordGenerationStyle.RandomCharacters, Length = 20 };

        for (var i = 0; i < Iterations; i++)
            Assert.That(_generator.Generate(policy), Has.Length.EqualTo(20));
    }

    [Test]
    public void Generate_RandomCharacters_AlwaysSatisfiesEveryMinimum()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            Length = 14,
            MinimumUppercase = 3,
            MinimumLowercase = 4,
            MinimumDigits = 2,
            MinimumSymbols = 2
        };

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(password.Count(char.IsUpper), Is.GreaterThanOrEqualTo(3), password);
                Assert.That(password.Count(char.IsLower), Is.GreaterThanOrEqualTo(4), password);
                Assert.That(password.Count(char.IsDigit), Is.GreaterThanOrEqualTo(2), password);
                Assert.That(password.Count(c => policy.PermittedSymbols.Contains(c)), Is.GreaterThanOrEqualTo(2), password);
            }
        }
    }

    [Test]
    public void Generate_RandomCharacters_ExcludingAmbiguousCharacters_LeavesThemOut()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            Length = 24,
            ExcludeAmbiguousCharacters = true
        };

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            Assert.That(password.Any(c => PasswordGenerationPolicy.AmbiguousCharacters.Contains(c)), Is.False, password);
        }
    }

    [Test]
    public void Generate_RandomCharacters_NotExcludingAmbiguousCharacters_CanUseThem()
    {
        // The mirror of the test above. Without it, a generator that excluded them unconditionally would pass
        // the exclusion test while quietly ignoring the setting.
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            Length = 24,
            ExcludeAmbiguousCharacters = false,
            // The default symbol set does not contain the pipe, so permit it explicitly: otherwise this would
            // be asserting that the exclusion setting can restore a character the policy never permitted.
            PermittedSymbols = PasswordGenerationPolicy.DefaultSymbols + "|"
        };

        var seen = new HashSet<char>();
        for (var i = 0; i < Iterations; i++)
            seen.UnionWith(_generator.Generate(policy));

        Assert.That(PasswordGenerationPolicy.AmbiguousCharacters.All(seen.Contains), Is.True,
            $"Characters never drawn: {new string(PasswordGenerationPolicy.AmbiguousCharacters.Where(c => !seen.Contains(c)).ToArray())}");
    }

    [Test]
    public void Generate_RandomCharacters_UsesOnlyThePermittedSymbols()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            Length = 20,
            PermittedSymbols = "@#"
        };

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            var symbols = password.Where(c => !char.IsLetterOrDigit(c)).ToList();
            Assert.That(symbols.All(c => c is '@' or '#'), Is.True, password);
        }
    }

    [Test]
    public void Generate_RandomCharacters_DoesNotPlaceTheRequiredClassesInAFixedOrder()
    {
        // Satisfying the minimums by writing them at the front and filling the rest would pass every test above
        // while making the first characters of every password predictable by class. Only the shuffle prevents
        // that, so it gets its own test.
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            Length = 12,
            MinimumUppercase = 1,
            MinimumLowercase = 1,
            MinimumDigits = 1,
            MinimumSymbols = 1
        };

        // The leading positions specifically: those are where the required characters are placed before the
        // shuffle moves them, so this is the only place the omission shows.
        const int RequiredPositions = 4;
        var classesSeen = new HashSet<string>[RequiredPositions];
        for (var position = 0; position < RequiredPositions; position++)
            classesSeen[position] = [];

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            for (var position = 0; position < RequiredPositions; position++)
            {
                var c = password[position];
                classesSeen[position].Add(char.IsUpper(c) ? "upper" : char.IsLower(c) ? "lower" : char.IsDigit(c) ? "digit" : "symbol");
            }
        }

        for (var position = 0; position < RequiredPositions; position++)
            Assert.That(classesSeen[position], Has.Count.EqualTo(4),
                $"Position {position} only ever held: {string.Join(", ", classesSeen[position])}. Every category should be able to appear anywhere.");
    }

    [Test]
    public void Generate_RandomCharacters_DrawsFromThePoolWithoutObviousBias()
    {
        // A modulo-based index draw skews towards the low end of the pool. This will not detect a subtle bias,
        // and does not pretend to; it detects the gross one that a hand-rolled draw produces.
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            Length = 32,
            MinimumUppercase = 0,
            MinimumLowercase = 32,
            MinimumDigits = 0,
            MinimumSymbols = 0,
            ExcludeAmbiguousCharacters = false
        };

        var counts = new Dictionary<char, int>();
        for (var i = 0; i < 2000; i++)
            foreach (var c in _generator.Generate(policy))
                counts[c] = counts.GetValueOrDefault(c) + 1;

        Assert.That(counts, Has.Count.EqualTo(26), "Every lowercase letter should be drawn.");

        var expected = counts.Values.Sum() / 26d;
        var worst = counts.MinBy(kvp => kvp.Value);
        var best = counts.MaxBy(kvp => kvp.Value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(worst.Value, Is.GreaterThan(expected * 0.7), $"'{worst.Key}' drawn far less often than expected.");
            Assert.That(best.Value, Is.LessThan(expected * 1.3), $"'{best.Key}' drawn far more often than expected.");
        }
    }

    [Test]
    public void Generate_CalledRepeatedly_DoesNotRepeatItself()
    {
        var policy = new PasswordGenerationPolicy();
        var generated = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < Iterations; i++)
            generated.Add(_generator.Generate(policy));

        Assert.That(generated, Has.Count.EqualTo(Iterations));
    }

    [Test]
    public void Generate_MinimumsExceedingTheLength_Throws()
    {
        // Fast and hard, rather than silently producing something that does not satisfy the configuration. The
        // administrator has asked for something impossible and needs to be told.
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            Length = 6,
            MinimumUppercase = 3,
            MinimumLowercase = 3,
            MinimumDigits = 3,
            MinimumSymbols = 3
        };

        Assert.Throws<ArgumentException>(() => _generator.Generate(policy));
    }

    [Test]
    public void Generate_RequiringSymbolsWithNonePermitted_Throws()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            MinimumSymbols = 1,
            PermittedSymbols = string.Empty
        };

        Assert.Throws<ArgumentException>(() => _generator.Generate(policy));
    }
    #endregion

    #region words
    [Test]
    public void Generate_Words_ProducesTheRequestedNumberOfWords()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.Words,
            WordCount = 4,
            WordSeparator = PasswordWordSeparator.Hyphen,
            WordCapitalisation = PasswordWordCapitalisation.EachWord,
            AppendedDigitCount = 0
        };

        for (var i = 0; i < Iterations; i++)
        {
            var parts = _generator.Generate(policy).Split('-');
            Assert.That(parts, Has.Length.EqualTo(4));
            Assert.That(parts.All(p => p.Length is >= 4 and <= 6), Is.True, string.Join('-', parts));
        }
    }

    [Test]
    public void Generate_Words_EachWordCapitalisation_CapitalisesEveryWord()
    {
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.EachWord);

        for (var i = 0; i < Iterations; i++)
        {
            var words = _generator.Generate(policy).Split('-');
            Assert.That(words.All(w => char.IsUpper(w[0]) && w.Skip(1).All(char.IsLower)), Is.True, string.Join('-', words));
        }
    }

    [Test]
    public void Generate_Words_FirstWordOnlyCapitalisation_CapitalisesOnlyTheFirst()
    {
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.FirstWordOnly);

        for (var i = 0; i < Iterations; i++)
        {
            var words = _generator.Generate(policy).Split('-');
            using (Assert.EnterMultipleScope())
            {
                Assert.That(char.IsUpper(words[0][0]), Is.True, words[0]);
                Assert.That(words.Skip(1).All(w => w.All(char.IsLower)), Is.True, string.Join('-', words));
            }
        }
    }

    [Test]
    public void Generate_Words_RandomWordCapitalisation_CapitalisesExactlyOneAndMovesItAround()
    {
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.RandomWord);
        var capitalisedPositions = new HashSet<int>();

        for (var i = 0; i < Iterations; i++)
        {
            var words = _generator.Generate(policy).Split('-');
            var capitalised = words.Select((w, index) => (w, index)).Where(x => char.IsUpper(x.w[0])).ToList();
            Assert.That(capitalised, Has.Count.EqualTo(1), string.Join('-', words));
            capitalisedPositions.Add(capitalised[0].index);
        }

        Assert.That(capitalisedPositions, Has.Count.EqualTo(policy.WordCount),
            "Every word position should be able to be the capitalised one.");
    }

    [Test]
    public void Generate_Words_LowercaseCapitalisation_LeavesEveryWordLowercase()
    {
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.Lowercase);

        for (var i = 0; i < Iterations; i++)
            Assert.That(_generator.Generate(policy).All(c => !char.IsUpper(c)), Is.True);
    }

    [Test]
    public void Generate_Words_UppercaseCapitalisation_UppercasesEverything()
    {
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.Uppercase);

        for (var i = 0; i < Iterations; i++)
            Assert.That(_generator.Generate(policy).All(c => !char.IsLower(c)), Is.True);
    }

    [Test]
    public void Generate_Words_NoSeparator_RunsTheWordsTogether()
    {
        var policy = WordPolicy(PasswordWordSeparator.None, PasswordWordCapitalisation.EachWord);

        for (var i = 0; i < Iterations; i++)
            Assert.That(_generator.Generate(policy).All(char.IsLetter), Is.True);
    }

    [Test]
    public void Generate_Words_DigitSeparator_PutsADigitBetweenEachPair()
    {
        var policy = WordPolicy(PasswordWordSeparator.Digit, PasswordWordCapitalisation.Lowercase);
        policy.WordCount = 3;

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            Assert.That(password.Count(char.IsDigit), Is.EqualTo(2), password);
        }
    }

    [Test]
    public void Generate_Words_RandomSymbolSeparator_UsesOnlyPermittedSymbols()
    {
        var policy = WordPolicy(PasswordWordSeparator.RandomSymbol, PasswordWordCapitalisation.Lowercase);
        policy.PermittedSymbols = "@#";
        policy.WordCount = 3;

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            var symbols = password.Where(c => !char.IsLetterOrDigit(c)).ToList();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(symbols, Has.Count.EqualTo(2), password);
                Assert.That(symbols.All(c => c is '@' or '#'), Is.True, password);
            }
        }
    }

    [Test]
    public void Generate_Words_AppendedDigits_AppendsExactlyThatMany()
    {
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.EachWord);
        policy.AppendedDigitCount = 3;

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(password.Count(char.IsDigit), Is.EqualTo(3), password);
                Assert.That(password[^3..].All(char.IsDigit), Is.True, password);
            }
        }
    }

    [Test]
    public void Generate_Words_AppendedSymbol_EndsWithOne()
    {
        var policy = WordPolicy(PasswordWordSeparator.None, PasswordWordCapitalisation.EachWord);
        policy.AppendedDigitCount = 0;
        policy.AppendSymbol = true;
        policy.PermittedSymbols = "@#";

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            Assert.That(password[^1], Is.AnyOf('@', '#'), password);
        }
    }

    [Test]
    public void Generate_Words_ExcludingAmbiguousCharacters_LeavesThemOutOfTheAppendedDigits()
    {
        // The words themselves are drawn whole from a list that has no ambiguous characters in it, so the only
        // place the setting can bite is what gets appended or used as a separator.
        var policy = WordPolicy(PasswordWordSeparator.Digit, PasswordWordCapitalisation.Lowercase);
        policy.AppendedDigitCount = 4;
        policy.ExcludeAmbiguousCharacters = true;

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            Assert.That(password.Any(c => c is '1' or '0'), Is.False, password);
        }
    }

    [Test]
    public void Generate_Words_DrawsEveryWordInTheListEventually()
    {
        // Proves the whole list is reachable. A draw that could never select the last entry (a classic
        // off-by-one on the index bound) would cost a fraction of a bit and be invisible otherwise.
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.Lowercase);
        policy.WordCount = 8;
        policy.AppendedDigitCount = 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var attempts = 0;
        while (seen.Count < PasswordWordList.Words.Count && attempts < 20_000)
        {
            seen.UnionWith(_generator.Generate(policy).Split('-'));
            attempts++;
        }

        Assert.That(seen, Has.Count.EqualTo(PasswordWordList.Words.Count));
    }
    #endregion

    #region pronounceable
    [Test]
    public void Generate_Pronounceable_ProducesTheRequestedNumberOfLetters()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.Pronounceable,
            Length = 14,
            AppendedDigitCount = 0
        };

        for (var i = 0; i < Iterations; i++)
            Assert.That(_generator.Generate(policy).Count(char.IsLetter), Is.EqualTo(14));
    }

    [Test]
    public void Generate_Pronounceable_AlternatesConsonantsAndVowels()
    {
        const string vowels = "aeiou";
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.Pronounceable,
            Length = 12,
            AppendedDigitCount = 0
        };

        for (var i = 0; i < Iterations; i++)
        {
            var letters = _generator.Generate(policy).Where(char.IsLetter).ToList();
            for (var position = 0; position < letters.Count; position++)
                Assert.That(vowels.Contains(letters[position]), Is.EqualTo(position % 2 == 1),
                    $"Position {position} of '{new string(letters.ToArray())}' breaks the alternation.");
        }
    }

    [Test]
    public void Generate_Pronounceable_BreaksLongOutputIntoReadableGroups()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.Pronounceable,
            Length = 14,
            AppendedDigitCount = 0
        };

        for (var i = 0; i < Iterations; i++)
        {
            var password = _generator.Generate(policy);
            Assert.That(password, Does.Contain("-"), password);
            Assert.That(password.Split('-').All(g => g.Length is > 0 and <= 6), Is.True, password);
        }
    }

    [Test]
    public void Generate_Pronounceable_ShortOutput_IsNotBrokenUp()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.Pronounceable,
            Length = 6,
            AppendedDigitCount = 0
        };

        for (var i = 0; i < Iterations; i++)
            Assert.That(_generator.Generate(policy), Does.Not.Contain("-"));
    }

    [Test]
    public void Generate_Pronounceable_ExcludingAmbiguousCharacters_LeavesThemOut()
    {
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.Pronounceable,
            Length = 16,
            AppendedDigitCount = 4,
            ExcludeAmbiguousCharacters = true
        };

        for (var i = 0; i < Iterations; i++)
            Assert.That(_generator.Generate(policy).Any(c => PasswordGenerationPolicy.AmbiguousCharacters.Contains(c)), Is.False);
    }
    #endregion

    #region assessment
    [Test]
    public void Assess_LowercaseWordsWithAHyphen_AgainstActiveDirectoryComplexity_IsRejected()
    {
        // The trap the whole assessment exists for. Lowercase words joined by hyphens offer two character
        // categories where a stock Active Directory domain wants three, so every account would be rejected.
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.Lowercase);
        policy.AppendedDigitCount = 0;

        var assessment = _generator.Assess(policy, ActiveDirectoryDefaultPolicy());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assessment.IsUsable, Is.False);
            Assert.That(assessment.GuaranteedCharacterClassCount, Is.EqualTo(2));
            Assert.That(assessment.Problems, Is.Not.Empty);
        }
    }

    [Test]
    public void Assess_LowercaseWordsWithAppendedDigits_MeetsActiveDirectoryComplexity()
    {
        // The same configuration with the default appended digits: lowercase, symbol and digit is three
        // categories, which clears the bar.
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.Lowercase);
        policy.AppendedDigitCount = 2;

        var assessment = _generator.Assess(policy, ActiveDirectoryDefaultPolicy());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assessment.IsUsable, Is.True, string.Join(" ", assessment.Problems));
            Assert.That(assessment.GuaranteedCharacterClassCount, Is.GreaterThanOrEqualTo(3));
        }
    }

    [Test]
    public void Assess_TooShortForTheTargetMinimumLength_IsRejected()
    {
        var policy = new PasswordGenerationPolicy { Style = PasswordGenerationStyle.RandomCharacters, Length = 8 };
        var target = new ConnectedSystemPasswordPolicy { MinimumLength = 20 };

        var assessment = _generator.Assess(policy, target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assessment.IsUsable, Is.False);
            Assert.That(assessment.GuaranteedMinimumLength, Is.EqualTo(8));
        }
    }

    [Test]
    public void Assess_ImpossibleMinimums_IsRejectedRatherThanThrowing()
    {
        // Assess is what the configuration screen calls on every keystroke, so an unsatisfiable policy has to
        // come back as a problem to display rather than as an exception.
        var policy = new PasswordGenerationPolicy
        {
            Style = PasswordGenerationStyle.RandomCharacters,
            Length = 4,
            MinimumUppercase = 2,
            MinimumLowercase = 2,
            MinimumDigits = 2,
            MinimumSymbols = 2
        };

        Assert.That(_generator.Assess(policy, null).IsUsable, Is.False);
    }

    [Test]
    public void Assess_NoDiscoveredPolicy_StillReportsWhatTheConfigurationProduces()
    {
        // Nothing discovered is not a reason to say nothing: the length, categories and entropy are properties
        // of the configuration alone, and are the numbers the administrator is looking at.
        var assessment = _generator.Assess(new PasswordGenerationPolicy(), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assessment.IsUsable, Is.True, string.Join(" ", assessment.Problems));
            Assert.That(assessment.GuaranteedMinimumLength, Is.EqualTo(16));
            Assert.That(assessment.EntropyBits, Is.GreaterThan(80d));
        }
    }

    [Test]
    public void Assess_WordStyleEntropy_FollowsTheShippedListSize()
    {
        // The readout has to come from the list that actually ships, so it cannot drift if the list is edited.
        var policy = WordPolicy(PasswordWordSeparator.Hyphen, PasswordWordCapitalisation.EachWord);
        policy.WordCount = 4;
        policy.AppendedDigitCount = 0;

        var assessment = _generator.Assess(policy, null);

        Assert.That(assessment.EntropyBits, Is.EqualTo(4 * Math.Log2(PasswordWordList.Words.Count)).Within(0.001));
    }

    [Test]
    public void Assess_RequiringMoreCategoriesThanAsciiCanOffer_IsRejected()
    {
        // Active Directory recognises five categories, of which one is alphabetic characters with no case. JIM
        // generates ASCII, so it can reach four; a target demanding five cannot be satisfied and saying so is
        // better than generating something that is rejected on every account.
        var target = ActiveDirectoryDefaultPolicy();
        target.RequiredCharacterClassCount = 5;

        var assessment = _generator.Assess(new PasswordGenerationPolicy(), target);

        Assert.That(assessment.IsUsable, Is.False);
    }

    [Test]
    public void Assess_GuaranteedCharacterClasses_AreActuallyPresentInEveryGeneratedPassword()
    {
        // The assessment works the categories out analytically, and the generator produces them independently.
        // This is what stops the two drifting apart: whatever Assess promises, Generate has to deliver, across
        // every combination of the two axes.
        foreach (var separator in Enum.GetValues<PasswordWordSeparator>())
        foreach (var capitalisation in Enum.GetValues<PasswordWordCapitalisation>())
        {
            var policy = WordPolicy(separator, capitalisation);
            var promised = _generator.Assess(policy, null).GuaranteedCharacterClasses;

            for (var i = 0; i < 50; i++)
            {
                var password = _generator.Generate(policy);
                var actual = ClassesIn(password, policy.PermittedSymbols);
                Assert.That((promised & actual), Is.EqualTo(promised),
                    $"'{password}' ({separator}, {capitalisation}) is missing categories the assessment promised.");
            }
        }
    }

    [Test]
    public void Assess_GuaranteedMinimumLength_IsNeverLongerThanWhatIsGenerated()
    {
        foreach (var separator in Enum.GetValues<PasswordWordSeparator>())
        foreach (var capitalisation in Enum.GetValues<PasswordWordCapitalisation>())
        {
            var policy = WordPolicy(separator, capitalisation);
            var promised = _generator.Assess(policy, null).GuaranteedMinimumLength;

            for (var i = 0; i < 50; i++)
            {
                var password = _generator.Generate(policy);
                Assert.That(password, Has.Length.GreaterThanOrEqualTo(promised),
                    $"'{password}' ({separator}, {capitalisation}) is shorter than the promised minimum.");
            }
        }
    }
    #endregion

    #region supplied passwords
    [Test]
    public void AssessSupplied_NothingTyped_IsNotUsable()
    {
        // The portal calls this while the administrator is still typing, so an empty field has to come back as a
        // problem to display rather than as an exception or a usable verdict.
        foreach (var nothing in new[] { null, string.Empty, "   " })
            Assert.That(_generator.AssessSupplied(nothing, null).IsUsable, Is.False, $"'{nothing}' was treated as a password.");
    }

    [Test]
    public void AssessSupplied_NoDiscoveredPolicy_ReportsWhatThePasswordContainsAndNoProblems()
    {
        // A floor JIM could not read is not a failure to report against. The length and categories are properties
        // of the password alone and are what the administrator is looking at.
        var assessment = _generator.AssessSupplied("Brown-Chicken-Ladder-47", null);

        Assert.Multiple(() =>
        {
            Assert.That(assessment.IsUsable, Is.True, string.Join(" ", assessment.Problems));
            Assert.That(assessment.Length, Is.EqualTo(23));
            Assert.That(assessment.CharacterClasses, Is.EqualTo(
                PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol));
        });
    }

    [Test]
    public void AssessSupplied_ShorterThanTheTargetMinimumLength_IsRejected()
    {
        var target = new ConnectedSystemPasswordPolicy { MinimumLength = 20 };

        var assessment = _generator.AssessSupplied("Sh0rt-One!", target);

        Assert.Multiple(() =>
        {
            Assert.That(assessment.IsUsable, Is.False);
            Assert.That(assessment.Length, Is.EqualTo(10));
        });
    }

    [Test]
    public void AssessSupplied_TooFewCharacterCategoriesForTheTarget_IsRejected()
    {
        // The same trap the generator assessment exists for, reached from the other direction: a password that
        // looks perfectly reasonable and that a stock Active Directory domain refuses on every account.
        var assessment = _generator.AssessSupplied("brownchickenladder", ActiveDirectoryDefaultPolicy());

        Assert.Multiple(() =>
        {
            Assert.That(assessment.IsUsable, Is.False);
            Assert.That(assessment.CharacterClassCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void AssessSupplied_MeetingActiveDirectoryComplexity_IsUsable()
    {
        var assessment = _generator.AssessSupplied("Brown-Chicken-Ladder-47", ActiveDirectoryDefaultPolicy());

        Assert.Multiple(() =>
        {
            Assert.That(assessment.IsUsable, Is.True, string.Join(" ", assessment.Problems));
            Assert.That(assessment.CharacterClassCount, Is.GreaterThanOrEqualTo(3));
        });
    }

    [Test]
    public void AssessSupplied_CategoriesTheTargetDoesNotRecognise_DoNotCountTowardsItsComplexityRule()
    {
        // A category one system does not count cannot help satisfy that system's "at least N categories", which is
        // the same reasoning the reconciliation applies when combining several systems.
        var target = ActiveDirectoryDefaultPolicy();
        target.RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase;

        var assessment = _generator.AssessSupplied("Brown-Chicken-Ladder-47", target);

        Assert.That(assessment.IsUsable, Is.False);
    }

    [Test]
    public void AssessSupplied_NoRecognisedCategoriesDiscovered_CountsEveryCategory()
    {
        // Reading a silence as a denial would reject a password the target would accept. Where the system did not
        // say which categories it counts, every category counts, exactly as it does for a generator configuration.
        var target = ActiveDirectoryDefaultPolicy();
        target.RecognisedCharacterClasses = PasswordCharacterClasses.None;

        Assert.That(_generator.AssessSupplied("Brown-Chicken-Ladder-47", target).IsUsable, Is.True);
    }

    [Test]
    public void AssessSupplied_Problems_NeverRepeatThePassword()
    {
        // The problems are shown in the portal, written into Activities and read by whoever is diagnosing a parked
        // account. None of those places may carry the password, and the assessment is the one place holding it.
        const string password = "Brown-Chicken-Ladder-47";
        var target = new ConnectedSystemPasswordPolicy
        {
            MinimumLength = 64,
            ComplexityRequired = true,
            RequiredCharacterClassCount = 5,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase
        };

        var assessment = _generator.AssessSupplied(password, target);

        Assert.That(assessment.Problems, Is.Not.Empty);
        foreach (var problem in assessment.Problems)
            Assert.That(problem, Does.Not.Contain(password));
    }

    [Test]
    public void AssessSupplied_AGeneratedPassword_ContainsEveryCategoryTheGeneratorPromised()
    {
        // Ties the two assessments together. Whatever Assess promises a configuration will produce, reading a real
        // generated password back has to find; if the two ever disagree, one of them is lying to an administrator.
        foreach (var separator in Enum.GetValues<PasswordWordSeparator>())
        foreach (var capitalisation in Enum.GetValues<PasswordWordCapitalisation>())
        {
            var policy = WordPolicy(separator, capitalisation);
            var promised = _generator.Assess(policy, null).GuaranteedCharacterClasses;

            for (var i = 0; i < 20; i++)
            {
                var password = _generator.Generate(policy);
                var found = _generator.AssessSupplied(password, null).CharacterClasses;

                Assert.That(found & promised, Is.EqualTo(promised),
                    $"'{password}' ({separator}, {capitalisation}) is missing categories the assessment promised.");
            }
        }
    }
    #endregion

    #region derivation
    [Test]
    public void DeriveFrom_NoDiscoveredPolicy_ProducesSomethingUsable()
    {
        var derived = _generator.DeriveFrom(null);

        Assert.That(_generator.Assess(derived, null).IsUsable, Is.True);
        Assert.DoesNotThrow(() => _generator.Generate(derived));
    }

    [Test]
    public void DeriveFrom_ALongMinimumLength_ProducesAtLeastThatMany()
    {
        var target = new ConnectedSystemPasswordPolicy { MinimumLength = 24 };

        var derived = _generator.DeriveFrom(target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(derived.Length, Is.GreaterThanOrEqualTo(24));
            Assert.That(_generator.Assess(derived, target).IsUsable, Is.True);
        }
    }

    [Test]
    public void DeriveFrom_AShortMinimumLength_DoesNotShortenTheDefault()
    {
        // A target that will accept eight characters is not a reason to generate eight.
        var derived = _generator.DeriveFrom(new ConnectedSystemPasswordPolicy { MinimumLength = 8 });

        Assert.That(derived.Length, Is.EqualTo(new PasswordGenerationPolicy().Length));
    }

    [Test]
    public void DeriveFrom_ActiveDirectoryComplexity_ProducesAPolicyThatSatisfiesIt()
    {
        var target = ActiveDirectoryDefaultPolicy();

        var derived = _generator.DeriveFrom(target);
        var assessment = _generator.Assess(derived, target);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(assessment.IsUsable, Is.True, string.Join(" ", assessment.Problems));
            Assert.That(assessment.GuaranteedCharacterClassCount, Is.GreaterThanOrEqualTo(3));
        }
    }
    #endregion

    #region helpers
    private static PasswordGenerationPolicy WordPolicy(PasswordWordSeparator separator, PasswordWordCapitalisation capitalisation) =>
        new()
        {
            Style = PasswordGenerationStyle.Words,
            WordCount = 4,
            WordSeparator = separator,
            WordCapitalisation = capitalisation,
            AppendedDigitCount = 0
        };

    private static ConnectedSystemPasswordPolicy ActiveDirectoryDefaultPolicy() =>
        new()
        {
            MinimumLength = 7,
            ComplexityRequired = true,
            RequiredCharacterClassCount = 3,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                                         PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol |
                                         PasswordCharacterClasses.OtherUnicodeLetter
        };

    private static PasswordCharacterClasses ClassesIn(string password, string permittedSymbols)
    {
        var classes = PasswordCharacterClasses.None;
        if (password.Any(char.IsUpper)) classes |= PasswordCharacterClasses.Uppercase;
        if (password.Any(char.IsLower)) classes |= PasswordCharacterClasses.Lowercase;
        if (password.Any(char.IsDigit)) classes |= PasswordCharacterClasses.Digit;
        if (password.Any(c => permittedSymbols.Contains(c) || c is '-' or '.' or '_')) classes |= PasswordCharacterClasses.Symbol;
        return classes;
    }
    #endregion
}
