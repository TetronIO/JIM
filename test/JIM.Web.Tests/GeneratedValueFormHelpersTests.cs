// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Numerics;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Pure display logic for the "JIM generates it" Attribute Flow form (Unique Value Generation, #242, Phase 3):
/// room-remaining, row summary, and the Random token's value-space/clash-likelihood hints. These are worth
/// pinning independently of the Blazor component that renders them, per test/CLAUDE.md's guidance to put pure
/// logic in small testable helpers.
/// </summary>
[TestFixture]
public class GeneratedValueFormHelpersTests
{
    [TestCase(AttributeDataType.Number, true)]
    [TestCase(AttributeDataType.LongNumber, true)]
    [TestCase(AttributeDataType.Text, false)]
    [TestCase(AttributeDataType.Boolean, false)]
    [TestCase(null, false)]
    public void IsNumberTarget_ReturnsExpected(AttributeDataType? type, bool expected)
    {
        Assert.That(GeneratedValueFormHelpers.IsNumberTarget(type), Is.EqualTo(expected));
    }

    [Test]
    public void GetSequenceRoomRemainingText_WidthElevenStartOneHundredThousandFourHundredFiftySix_MatchesMockup()
    {
        // Approved mockup screen 02: Width 11, Start at 100456 -> "Room for 99,999,899,543 more numbers".
        var result = GeneratedValueFormHelpers.GetSequenceRoomRemainingText(100456, 11);

        Assert.That(result, Is.EqualTo("Room for 99,999,899,543 more numbers"));
    }

    [Test]
    public void GetSequenceRoomRemainingText_WidthSixStartOneHundredThousandFourHundredFiftySix_MatchesMockup()
    {
        // Approved mockup screen 03: Width 6, Start at 100456 -> "Room for 899,543".
        var result = GeneratedValueFormHelpers.GetSequenceRoomRemainingText(100456, 6);

        Assert.That(result, Is.EqualTo("Room for 899,543 more numbers"));
    }

    [Test]
    public void GetSequenceRoomRemainingText_StartExceedsWidth_ReturnsNull()
    {
        var result = GeneratedValueFormHelpers.GetSequenceRoomRemainingText(1_000_000, 4);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetSequenceRoomRemainingText_WidthLessThanOne_ReturnsNull()
    {
        var result = GeneratedValueFormHelpers.GetSequenceRoomRemainingText(1, 0);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetRowSummary_OnlyIfTakenNumber_ReturnsExpected()
    {
        var generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
            SuffixStyle = GeneratedValueSuffixStyle.Number
        };

        Assert.That(GeneratedValueFormHelpers.GetRowSummary(generation), Is.EqualTo("Only if taken · number"));
    }

    [Test]
    public void GetRowSummary_OnlyIfTakenLetter_ReturnsExpected()
    {
        var generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
            SuffixStyle = GeneratedValueSuffixStyle.Letter
        };

        Assert.That(GeneratedValueFormHelpers.GetRowSummary(generation), Is.EqualTo("Only if taken · letter"));
    }

    [Test]
    public void GetRowSummary_SequenceWithFixedWidth_ReturnsExpected()
    {
        var generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Sequence,
            FixedWidth = 11
        };

        Assert.That(GeneratedValueFormHelpers.GetRowSummary(generation), Is.EqualTo("Sequence · width 11"));
    }

    [Test]
    public void GetRowSummary_SequenceWithoutFixedWidth_ReturnsBareLabel()
    {
        var generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Sequence,
            FixedWidth = null
        };

        Assert.That(GeneratedValueFormHelpers.GetRowSummary(generation), Is.EqualTo("Sequence"));
    }

    [TestCase(GeneratedValueRandomFormat.Guid, null, "Random · GUID")]
    [TestCase(GeneratedValueRandomFormat.Hex, 8, "Random · hex 8")]
    [TestCase(GeneratedValueRandomFormat.Digits, 6, "Random · digits 6")]
    public void GetRowSummary_RandomFormats_ReturnExpected(GeneratedValueRandomFormat format, int? length, string expected)
    {
        var generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Random,
            RandomFormat = format,
            RandomLength = length
        };

        Assert.That(GeneratedValueFormHelpers.GetRowSummary(generation), Is.EqualTo(expected));
    }

    [TestCase(500, "500")]
    [TestCase(1_000, "1 thousand")]
    [TestCase(1_000_000, "1 million")]
    [TestCase(1_000_000_000, "1 billion")]
    public void FormatApproximateCount_RoundNumbers_ReturnExpected(long value, string expected)
    {
        Assert.That(GeneratedValueFormHelpers.FormatApproximateCount(new BigInteger(value)), Is.EqualTo(expected));
    }

    [Test]
    public void FormatApproximateCount_HexEightCharacters_MatchesMockupFigure()
    {
        // Approved mockup screen 03: Hex, 8 characters -> "4.3 billion values".
        var space = BigInteger.Pow(16, 8);

        Assert.That(GeneratedValueFormHelpers.FormatApproximateCount(space), Is.EqualTo("4.3 billion"));
    }

    [Test]
    public void DescribeClashLikelihood_HexEightCharacterSpace_DrawsAgain()
    {
        var space = BigInteger.Pow(16, 8);

        Assert.That(GeneratedValueFormHelpers.DescribeClashLikelihood(space), Is.EqualTo("clashes draw again"));
    }

    [Test]
    public void DescribeClashLikelihood_DigitsSixCharacterSpace_MatchesMockupThreshold()
    {
        // Approved mockup screen 03: Digits, 6 characters -> "clashes become likely past ~1,000 objects".
        var space = BigInteger.Pow(10, 6);

        Assert.That(GeneratedValueFormHelpers.DescribeClashLikelihood(space), Is.EqualTo("clashes become likely past ~1,000 objects"));
    }

    [Test]
    public void GetRandomFormatMenuHint_Guid_NoClashChecksNeeded()
    {
        var hint = GeneratedValueFormHelpers.GetRandomFormatMenuHint(GeneratedValueRandomFormat.Guid, null);

        Assert.That(hint, Does.Contain("no clash checks needed"));
    }

    [Test]
    public void GetRandomFormatMenuHint_HexEight_MatchesMockup()
    {
        var hint = GeneratedValueFormHelpers.GetRandomFormatMenuHint(GeneratedValueRandomFormat.Hex, 8);

        Assert.That(hint, Is.EqualTo("lower case · 4.3 billion values · clashes draw again"));
    }

    [Test]
    public void GetRandomFormatMenuHint_DigitsSix_MatchesMockup()
    {
        var hint = GeneratedValueFormHelpers.GetRandomFormatMenuHint(GeneratedValueRandomFormat.Digits, 6);

        Assert.That(hint, Is.EqualTo("1 million values · clashes become likely past ~1,000 objects"));
    }

    [TestCase(0)]
    [TestCase(null)]
    public void GetRandomFormatMenuHint_HexWithNoValidLength_ReturnsEmpty(int? length)
    {
        var hint = GeneratedValueFormHelpers.GetRandomFormatMenuHint(GeneratedValueRandomFormat.Hex, length);

        Assert.That(hint, Is.Empty);
    }

    [Test]
    public void IsGeneratedTargetDisabled_MultiValuedText_IsDisabled()
    {
        Assert.That(GeneratedValueFormHelpers.IsGeneratedTargetDisabled(AttributeDataType.Text, AttributePlurality.MultiValued), Is.True);
    }

    [Test]
    public void IsGeneratedTargetDisabled_SingleValuedBoolean_IsDisabled()
    {
        Assert.That(GeneratedValueFormHelpers.IsGeneratedTargetDisabled(AttributeDataType.Boolean, AttributePlurality.SingleValued), Is.True);
    }

    [TestCase(AttributeDataType.Text)]
    [TestCase(AttributeDataType.Number)]
    [TestCase(AttributeDataType.LongNumber)]
    public void IsGeneratedTargetDisabled_SingleValuedSupportedTypes_AreNotDisabled(AttributeDataType type)
    {
        Assert.That(GeneratedValueFormHelpers.IsGeneratedTargetDisabled(type, AttributePlurality.SingleValued), Is.False);
    }

    [Test]
    public void GetGeneratedTargetDisabledReason_SupportedSingleValuedTarget_ReturnsNull()
    {
        Assert.That(GeneratedValueFormHelpers.GetGeneratedTargetDisabledReason(AttributeDataType.Text, AttributePlurality.SingleValued), Is.Null);
    }

    [Test]
    public void GetGeneratedTargetDisabledReason_MultiValued_NamesMultiValuedFirst()
    {
        var reason = GeneratedValueFormHelpers.GetGeneratedTargetDisabledReason(AttributeDataType.Boolean, AttributePlurality.MultiValued);

        Assert.That(reason, Does.Contain("single-valued"));
    }

    [Test]
    public void GetGeneratedTargetDisabledReason_UnsupportedType_NamesTheType()
    {
        var reason = GeneratedValueFormHelpers.GetGeneratedTargetDisabledReason(AttributeDataType.DateTime, AttributePlurality.SingleValued);

        Assert.That(reason, Does.Contain("DateTime"));
    }

    [Test]
    public void DefaultMissingInputBehaviourForGeneratedMapping_IsContributeNoValue()
    {
        // "Wait until every input has a value" (plan Phase 3 point 4) is expressed as ContributeNoValue: the
        // mapping contributes nothing this run rather than generating from a partial input.
        Assert.That(GeneratedValueFormHelpers.DefaultMissingInputBehaviourForGeneratedMapping, Is.EqualTo(MissingInputBehaviour.ContributeNoValue));
    }
}
