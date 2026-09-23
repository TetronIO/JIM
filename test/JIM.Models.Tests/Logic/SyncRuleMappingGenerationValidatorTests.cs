// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

[TestFixture]
public class SyncRuleMappingGenerationValidatorTests
{
    private static SyncRuleMapping TextMapping(AttributePlurality plurality = AttributePlurality.SingleValued)
    {
        return new SyncRuleMapping
        {
            TargetMetaverseAttribute = new MetaverseAttribute { Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = plurality }
        };
    }

    private static SyncRuleMapping NumberMapping(AttributeDataType type = AttributeDataType.Number)
    {
        return new SyncRuleMapping
        {
            TargetMetaverseAttribute = new MetaverseAttribute { Name = "Employee Number", Type = type, AttributePlurality = AttributePlurality.SingleValued }
        };
    }

    private static SyncRuleMappingGeneration OnlyIfTaken()
    {
        return new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
    }

    // ---- No generation row: nothing to validate ----

    [Test]
    public void Validate_NoGeneration_ReturnsNoErrors()
    {
        var mapping = TextMapping();

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    // ---- Valid baseline cases, one per token kind ----

    [Test]
    public void Validate_ValidOnlyIfTakenOnTextTarget_ReturnsNoErrors()
    {
        var mapping = TextMapping();
        mapping.Generation = OnlyIfTaken();
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_ValidSequenceOnTextTarget_ReturnsNoErrors()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Sequence,
            SequenceStart = 1000,
            SequenceIncrement = 1,
            FixedWidth = 6,
            NeverReuse = true
        };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_ValidRandomGuidOnTextTarget_ReturnsNoErrors()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Random,
            RandomFormat = GeneratedValueRandomFormat.Guid
        };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_ValidRandomDigitsOnNumberTarget_ReturnsNoErrors()
    {
        var mapping = NumberMapping();
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Random,
            RandomFormat = GeneratedValueRandomFormat.Digits,
            RandomLength = 6
        };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_ValidSequenceOnNumberTarget_ReturnsNoErrors()
    {
        var mapping = NumberMapping();
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Sequence,
            SequenceStart = 1,
            SequenceIncrement = 1
        };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    // ---- Rule 1: single-valued target ----

    [Test]
    public void Validate_MultiValuedTarget_ReturnsError()
    {
        var mapping = TextMapping(AttributePlurality.MultiValued);
        mapping.Generation = OnlyIfTaken();
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("single-valued"));
    }

    // ---- Rule 2: Text or Number target only ----

    [Test]
    public void Validate_BooleanTarget_ReturnsError()
    {
        var mapping = new SyncRuleMapping
        {
            TargetMetaverseAttribute = new MetaverseAttribute { Name = "Active", Type = AttributeDataType.Boolean, AttributePlurality = AttributePlurality.SingleValued },
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Guid }
        };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("Text or Number"));
    }

    // ---- Rule 3: sources ----

    [Test]
    public void Validate_AttributeSource_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Guid };
        mapping.Sources.Add(new SyncRuleMappingSource { MetaverseAttribute = new MetaverseAttribute { Name = "First Name" } });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("single base expression"));
    }

    [Test]
    public void Validate_MoreThanOneExpressionSource_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Guid };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"Last Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("at most one source"));
    }

    [Test]
    public void Validate_OnlyIfTakenWithNoExpression_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = OnlyIfTaken();

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("\"Only if taken\" requires a base expression"));
    }

    [Test]
    public void Validate_SequenceWithNoExpression_ReturnsNoSourceError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.None.Contains("base expression"));
    }

    // ---- Rule 4: Number target constraints ----

    [Test]
    public void Validate_OnlyIfTakenOnNumberTarget_ReturnsError()
    {
        var mapping = NumberMapping();
        mapping.Generation = OnlyIfTaken();
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("cannot use \"Only if taken\""));
    }

    [Test]
    public void Validate_RandomGuidOnNumberTarget_ReturnsError()
    {
        var mapping = NumberMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Guid };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("must use the Digits format"));
    }

    [Test]
    public void Validate_NumberTargetWithBaseExpression_ReturnsError()
    {
        var mapping = NumberMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "\"EMP\"" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("cannot have a base expression"));
    }

    [Test]
    public void Validate_NumberTargetWithFixedWidth_ReturnsError()
    {
        var mapping = NumberMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, FixedWidth = 6 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("cannot be padded to a fixed width"));
    }

    [Test]
    public void Validate_NumberTargetWithSeparator_ReturnsError()
    {
        var mapping = NumberMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, Separator = "-" };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("cannot have a separator"));
    }

    [Test]
    public void Validate_DigitsRandomLengthExceedsNumberLimit_ReturnsError()
    {
        var mapping = NumberMapping();
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Random,
            RandomFormat = GeneratedValueRandomFormat.Digits,
            RandomLength = 10
        };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("at most 9 digits"));
    }

    [Test]
    public void Validate_DigitsRandomLengthExceedsLongNumberLimit_ReturnsError()
    {
        var mapping = NumberMapping(AttributeDataType.LongNumber);
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Random,
            RandomFormat = GeneratedValueRandomFormat.Digits,
            RandomLength = 19
        };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("at most 18 digits"));
    }

    [Test]
    public void Validate_DigitsRandomLengthWithinLongNumberLimit_ReturnsNoErrors()
    {
        var mapping = NumberMapping(AttributeDataType.LongNumber);
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.Random,
            RandomFormat = GeneratedValueRandomFormat.Digits,
            RandomLength = 18
        };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    // ---- Rule 5: only-if-taken suffix ----

    [Test]
    public void Validate_SuffixStartBelowOne_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken, SuffixStart = 0 };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("must start at 1 or higher"));
    }

    [Test]
    public void Validate_LetterSuffixStartAbove26_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
            SuffixStyle = GeneratedValueSuffixStyle.Letter,
            SuffixStart = 27
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("no higher than 26"));
    }

    [Test]
    public void Validate_LetterSuffixStartAt26_ReturnsNoErrors()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration
        {
            TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
            SuffixStyle = GeneratedValueSuffixStyle.Letter,
            SuffixStart = 26
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    // ---- Rule 6: sequence settings ----

    [Test]
    public void Validate_SequenceStartNegative_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = -1 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("sequence start value must be 0 or higher"));
    }

    [Test]
    public void Validate_SequenceIncrementBelowOne_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, SequenceIncrement = 0 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("sequence increment must be 1 or higher"));
    }

    [Test]
    public void Validate_SequenceFixedWidthOutOfRange_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, FixedWidth = 19 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("fixed width must be between 1 and 18"));
    }

    [Test]
    public void Validate_SequenceFixedWidthNarrowerThanStartDigits_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 10000, FixedWidth = 3 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("must be at least as wide as the sequence start value"));
    }

    [Test]
    public void Validate_SequenceWithNeverReuseOff_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, NeverReuse = false };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("cannot have \"Never reuse a value\" turned off"));
    }

    // ---- Rule 7: random settings ----

    [Test]
    public void Validate_RandomGuidWithLength_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Guid, RandomLength = 10 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("fixed length"));
    }

    [Test]
    public void Validate_RandomHexWithNoLength_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Hex };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("length between 1 and 64"));
    }

    [Test]
    public void Validate_RandomHexLengthOutOfRange_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Hex, RandomLength = 65 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("length between 1 and 64"));
    }

    [Test]
    public void Validate_RandomHexLengthAtBoundary_ReturnsNoErrors()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Hex, RandomLength = 64 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    // ---- Rule 8: separator ----

    [Test]
    public void Validate_SeparatorTooLong_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, Separator = "----" };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("1 to 3 characters"));
    }

    [Test]
    public void Validate_SeparatorContainsWhitespace_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, Separator = " -" };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("cannot contain whitespace"));
    }

    [Test]
    public void Validate_SeparatorContainsAtSign_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, Separator = "@" };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("cannot contain \"@\""));
    }

    [Test]
    public void Validate_SeparatorWithNoBaseExpression_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, Separator = "-" };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("only meaningful alongside a base expression"));
    }

    [Test]
    public void Validate_ValidSeparatorWithBaseExpression_ReturnsNoErrors()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, Separator = "-" };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }

    // ---- Rule 9: attempt limit ----

    [Test]
    public void Validate_AttemptLimitZero_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, AttemptLimit = 0 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("attempt limit must be between 1 and 100000"));
    }

    [Test]
    public void Validate_AttemptLimitTooHigh_ReturnsError()
    {
        var mapping = TextMapping();
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, AttemptLimit = 100001 };

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("attempt limit must be between 1 and 100000"));
    }

    [Test]
    public void Validate_AttemptLimitAtBoundaries_ReturnsNoErrors()
    {
        var lower = TextMapping();
        lower.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, AttemptLimit = 1 };
        var upper = TextMapping();
        upper.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, AttemptLimit = 100000 };

        Assert.That(SyncRuleMappingGenerationValidator.Validate(lower), Is.Empty);
        Assert.That(SyncRuleMappingGenerationValidator.Validate(upper), Is.Empty);
    }

    // ---- Rule 10: exclusions ----

    [Test]
    public void Validate_DuplicateExclusion_ReturnsError()
    {
        var mapping = TextMapping();
        var generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence };
        generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = 5 });
        generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = 5 });
        mapping.Generation = generation;

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Has.Some.Contains("cannot be excluded more than once"));
    }

    [Test]
    public void Validate_DistinctExclusions_ReturnsNoErrors()
    {
        var mapping = TextMapping();
        var generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence };
        generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = 5 });
        generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = 6 });
        mapping.Generation = generation;

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);

        Assert.That(errors, Is.Empty);
    }
}
