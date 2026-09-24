// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Pure candidate computation for Unique Value Generation (#242): placement, the only-if-taken suffix and
/// letter strategy, sequence rendering and width checking, and random token drawing. None of these touch a
/// repository or a reservation set; <see cref="UniqueValueGenerationServerResolveTests"/> covers the
/// orchestration around them.
/// </summary>
[TestFixture]
public class UniqueValueCandidateTests
{
    // ---- Placement (FR 3, 23) ----

    [Test]
    public void Place_BaseValueContainsAt_TokenGoesBeforeTheFirstAt()
    {
        var result = UniqueValueCandidates.Place("joe.bloggs@example.com", "1", null);
        Assert.That(result, Is.EqualTo("joe.bloggs1@example.com"));
    }

    [Test]
    public void Place_BaseValueHasNoAt_TokenIsAppended()
    {
        var result = UniqueValueCandidates.Place("joe.bloggs", "1", null);
        Assert.That(result, Is.EqualTo("joe.bloggs1"));
    }

    [Test]
    public void Place_WithSeparator_SeparatorSitsBetweenBaseAndToken()
    {
        var result = UniqueValueCandidates.Place("joe.bloggs", "1", "-");
        Assert.That(result, Is.EqualTo("joe.bloggs-1"));
    }

    [Test]
    public void Place_WithSeparatorAndAt_SeparatorSitsBeforeTheAt()
    {
        var result = UniqueValueCandidates.Place("joe.bloggs@example.com", "1", "-");
        Assert.That(result, Is.EqualTo("joe.bloggs-1@example.com"));
    }

    [Test]
    public void Place_NoBaseValue_TokenIsTheValueOnItsOwn()
    {
        var result = UniqueValueCandidates.Place(null, "G-100456", null);
        Assert.That(result, Is.EqualTo("G-100456"));
    }

    [Test]
    public void Place_NoToken_ReturnsTheBaseValueUnchanged()
    {
        var result = UniqueValueCandidates.Place("joe.bloggs", null, "-");
        Assert.That(result, Is.EqualTo("joe.bloggs"));
    }

    // ---- Only-if-taken candidate sequencing (FR 23) ----

    [Test]
    public void OnlyIfTakenCandidate_AttemptZero_ReturnsTheBareBaseValue()
    {
        var result = UniqueValueCandidates.OnlyIfTakenCandidate("joe.bloggs", 0, GeneratedValueSuffixStyle.Number, 1, null);
        Assert.That(result, Is.EqualTo("joe.bloggs"));
    }

    [Test]
    public void OnlyIfTakenCandidate_SubsequentAttempts_AppendIncrementingNumberSuffixes()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(UniqueValueCandidates.OnlyIfTakenCandidate("joe.bloggs", 1, GeneratedValueSuffixStyle.Number, 1, null), Is.EqualTo("joe.bloggs1"));
            Assert.That(UniqueValueCandidates.OnlyIfTakenCandidate("joe.bloggs", 2, GeneratedValueSuffixStyle.Number, 1, null), Is.EqualTo("joe.bloggs2"));
            Assert.That(UniqueValueCandidates.OnlyIfTakenCandidate("joe.bloggs", 3, GeneratedValueSuffixStyle.Number, 1, null), Is.EqualTo("joe.bloggs3"));
        }
    }

    [Test]
    public void OnlyIfTakenCandidate_NonDefaultSuffixStart_StartsCountingFromThere()
    {
        var result = UniqueValueCandidates.OnlyIfTakenCandidate("joe.bloggs", 1, GeneratedValueSuffixStyle.Number, 100, null);
        Assert.That(result, Is.EqualTo("joe.bloggs100"));
    }

    [Test]
    public void OnlyIfTakenCandidate_LetterStyle_AppendsLetters()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(UniqueValueCandidates.OnlyIfTakenCandidate("joe.bloggs", 1, GeneratedValueSuffixStyle.Letter, 1, null), Is.EqualTo("joe.bloggsa"));
            Assert.That(UniqueValueCandidates.OnlyIfTakenCandidate("joe.bloggs", 2, GeneratedValueSuffixStyle.Letter, 1, null), Is.EqualTo("joe.bloggsb"));
        }
    }

    // ---- Bijective base-26 letter strategy ----

    [TestCase(1, "a")]
    [TestCase(26, "z")]
    [TestCase(27, "aa")]
    [TestCase(52, "az")]
    [TestCase(53, "ba")]
    [TestCase(702, "zz")]
    [TestCase(703, "aaa")]
    public void ToBijectiveBase26Letters_KnownValues_RenderCorrectly(int n, string expected)
    {
        Assert.That(UniqueValueCandidates.ToBijectiveBase26Letters(n), Is.EqualTo(expected));
    }

    [Test]
    public void ToBijectiveBase26Letters_ZeroOrNegative_Throws()
    {
        Assert.That(() => UniqueValueCandidates.ToBijectiveBase26Letters(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    // ---- Sequence rendering, padding and overflow (FR 25) ----

    [Test]
    public void RenderSequenceNumber_NoFixedWidth_RendersUnpadded()
    {
        var (text, widthExceeded) = UniqueValueCandidates.RenderSequenceNumber(42, null, GeneratedValueWidthOverflowBehaviour.StopAndReport);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Is.EqualTo("42"));
            Assert.That(widthExceeded, Is.False);
        }
    }

    [Test]
    public void RenderSequenceNumber_FitsFixedWidth_ZeroPads()
    {
        var (text, widthExceeded) = UniqueValueCandidates.RenderSequenceNumber(456, 6, GeneratedValueWidthOverflowBehaviour.StopAndReport);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Is.EqualTo("000456"));
            Assert.That(widthExceeded, Is.False);
        }
    }

    [Test]
    public void RenderSequenceNumber_OutgrowsWidthWithStopAndReport_ReportsWidthExceeded()
    {
        var (text, widthExceeded) = UniqueValueCandidates.RenderSequenceNumber(1000000, 6, GeneratedValueWidthOverflowBehaviour.StopAndReport);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Is.EqualTo("1000000"));
            Assert.That(widthExceeded, Is.True);
        }
    }

    [Test]
    public void RenderSequenceNumber_OutgrowsWidthWithAllowLonger_RendersUnpaddedAndDoesNotReportExceeded()
    {
        var (text, widthExceeded) = UniqueValueCandidates.RenderSequenceNumber(1000000, 6, GeneratedValueWidthOverflowBehaviour.AllowLonger);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Is.EqualTo("1000000"));
            Assert.That(widthExceeded, Is.False);
        }
    }

    // ---- Random tokens (FR 27) ----

    [Test]
    public void GenerateRandomToken_Guid_IsLowerCaseThirtySixCharacters()
    {
        var token = UniqueValueCandidates.GenerateRandomToken(GeneratedValueRandomFormat.Guid, null, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(token, Has.Length.EqualTo(36));
            Assert.That(token, Is.EqualTo(token.ToLowerInvariant()));
            Assert.That(Guid.TryParse(token, out _), Is.True);
        }
    }

    [Test]
    public void GenerateRandomToken_Hex_IsLowerCaseHexOfTheConfiguredLength()
    {
        var token = UniqueValueCandidates.GenerateRandomToken(GeneratedValueRandomFormat.Hex, 12, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(token, Has.Length.EqualTo(12));
            Assert.That(token, Is.EqualTo(token.ToLowerInvariant()));
            Assert.That(token, Does.Match("^[0-9a-f]+$"));
        }
    }

    [Test]
    public void GenerateRandomToken_Digits_IsAllDigitsOfTheConfiguredLength()
    {
        var token = UniqueValueCandidates.GenerateRandomToken(GeneratedValueRandomFormat.Digits, 9, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(token, Has.Length.EqualTo(9));
            Assert.That(token, Does.Match("^[0-9]+$"));
        }
    }

    [Test]
    public void GenerateRandomToken_DigitsWithForceNonZeroLeadingDigit_NeverStartsWithZero()
    {
        for (var i = 0; i < 200; i++)
        {
            var token = UniqueValueCandidates.GenerateRandomToken(GeneratedValueRandomFormat.Digits, 5, true);
            Assert.That(token[0], Is.Not.EqualTo('0'), "a Number target's random digit token must never lose a leading zero to under-run its configured length");
        }
    }

    [Test]
    public void GenerateRandomToken_ManyDraws_AreDistinct()
    {
        // Draws are from System.Security.Cryptography.RandomNumberGenerator, never System.Random: many draws in
        // quick succession must not repeat or cluster the way a poorly-seeded PRNG could.
        var tokens = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
            tokens.Add(UniqueValueCandidates.GenerateRandomToken(GeneratedValueRandomFormat.Hex, 16, false));

        Assert.That(tokens, Has.Count.EqualTo(1000));
    }
}
