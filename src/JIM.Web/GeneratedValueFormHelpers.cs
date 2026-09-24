// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Numerics;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;

namespace JIM.Web;

/// <summary>
/// Pure display logic for the "Generated Value" Attribute Flow form (Unique Value Generation, #242, Phase 3):
/// the row's token pill text, the "room remaining" hint for fixed-width sequences, and the value-space and
/// clash-likelihood hints on the Random token's format menu. Kept free of Blazor/MudBlazor so it is directly
/// unit-testable; see <c>GeneratedValueFormHelpersTests</c>.
/// </summary>
public static class GeneratedValueFormHelpers
{
    /// <summary>
    /// The Missing Input Behaviour a newly created generated mapping defaults to: "Wait until every input has a
    /// value" ("If an input has no value", plan Phase 3 point 4). A plain Expression mapping keeps
    /// <see cref="MissingInputBehaviour.EvaluateAnyway"/> as its own default; this is a different default for a
    /// different mapping kind, not a change to that one. <see cref="MissingInputBehaviour.ContributeNoValue"/> is
    /// the enum member that means "wait": the mapping contributes nothing this run, so no value is generated from
    /// a partial input, and the next synchronisation tries again once every input is present.
    /// </summary>
    public const MissingInputBehaviour DefaultMissingInputBehaviourForGeneratedMapping = MissingInputBehaviour.ContributeNoValue;

    /// <summary>
    /// Whether a target attribute's data type is one of the two Number targets (<see cref="AttributeDataType.Number"/>,
    /// <see cref="AttributeDataType.LongNumber"/>) that <see cref="SyncRuleMappingGenerationValidator"/> restricts:
    /// no base expression, no "Only if taken", Random limited to Digits, no fixed-width padding.
    /// </summary>
    public static bool IsNumberTarget(AttributeDataType? type) =>
        type is AttributeDataType.Number or AttributeDataType.LongNumber;

    /// <summary>
    /// The "Room for N more numbers" hint under a fixed-width sequence's Width field: how many numbers remain
    /// between the sequence's start value and the width's ceiling (10^width - 1). Null when the width is not yet
    /// a valid digit count, or the start value already exceeds what the width can hold (the validator reports
    /// that as an error; this only renders the hint when there is a genuine number to show).
    /// </summary>
    public static string? GetSequenceRoomRemainingText(long sequenceStart, int fixedWidth)
    {
        if (fixedWidth < 1)
            return null;

        var ceiling = BigInteger.Pow(10, fixedWidth) - 1;
        var room = ceiling - sequenceStart;
        if (room < 0)
            return null;

        return $"Room for {room.ToString("N0", CultureInfo.InvariantCulture)} more numbers";
    }

    /// <summary>
    /// The row's token pill text, shown in the Source position after the base expression pill (if any): "+
    /// number if taken", "Sequence from 100456 · width 11", "Random hex · 8". No sequence state (next number,
    /// assigned/retired counts) yet: that arrives with the generation engine (Phase 2) and its state surfaces
    /// (a later work package).
    /// </summary>
    /// <param name="generation">The generated mapping's settings.</param>
    /// <param name="hasBaseExpression">
    /// Whether the row also carries a base expression pill; when true the token pill is prefixed "+ " to read
    /// as an addition to that pill, and stands alone with no prefix when there is no base expression.
    /// </param>
    public static string GetTokenPillText(SyncRuleMappingGeneration generation, bool hasBaseExpression)
    {
        var label = generation.TokenKind switch
        {
            GeneratedValueTokenKind.OnlyIfTaken =>
                $"{(generation.SuffixStyle == GeneratedValueSuffixStyle.Letter ? "letter" : "number")} if taken",

            GeneratedValueTokenKind.Sequence => generation.FixedWidth.HasValue
                ? $"Sequence from {generation.SequenceStart.ToString(CultureInfo.InvariantCulture)} · width {generation.FixedWidth.Value.ToString(CultureInfo.InvariantCulture)}"
                : $"Sequence from {generation.SequenceStart.ToString(CultureInfo.InvariantCulture)}",

            GeneratedValueTokenKind.Random => generation.RandomFormat switch
            {
                GeneratedValueRandomFormat.Guid => "Random GUID",
                GeneratedValueRandomFormat.Hex => $"Random hex · {generation.RandomLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
                GeneratedValueRandomFormat.Digits => $"Random digits · {generation.RandomLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}",
                _ => "Random"
            },

            _ => string.Empty
        };

        return hasBaseExpression ? $"+ {label}" : label;
    }

    /// <summary>
    /// The default Width a Sequence token's "Pad to a fixed width" switch fills in when first turned on:
    /// whichever is larger of 6 (a sensible starting point; "Room for 8 more numbers" at width 1 is useless)
    /// or the digit count of the current Start at value, so an administrator who has already set a large start
    /// value is not handed a width that value cannot fit (the existing "must be at least as wide as the
    /// sequence start value" validation reason covers Start at growing past this default afterwards).
    /// </summary>
    public static int GetDefaultFixedWidth(long sequenceStart)
    {
        var startDigits = Math.Abs(sequenceStart).ToString(CultureInfo.InvariantCulture).Length;
        return Math.Max(6, startDigits);
    }

    /// <summary>
    /// Formats a value space as an approximate, human-scale count ("4.3 billion", "1 million"), matching the
    /// Random token format menu's wording in the approved mockup.
    /// </summary>
    public static string FormatApproximateCount(BigInteger value)
    {
        (BigInteger Threshold, string Suffix)[] scale =
        [
            (BigInteger.Parse("1000000000000", CultureInfo.InvariantCulture), "trillion"),
            (1_000_000_000, "billion"),
            (1_000_000, "million"),
            (1_000, "thousand")
        ];

        foreach (var (threshold, suffix) in scale)
        {
            if (value >= threshold)
            {
                var scaled = (double)value / (double)threshold;
                return $"{scaled.ToString("0.#", CultureInfo.InvariantCulture)} {suffix}";
            }
        }

        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The clash-likelihood clause for a Random token's value space: a large space draws again on the rare
    /// clash, a small one names roughly how many objects before a clash becomes likely (the square root of the
    /// space, the birthday-paradox threshold), matching the two worked examples in the approved mockup (hex 8
    /// characters: "clashes draw again"; digits 6: "clashes become likely past ~1,000 objects").
    /// </summary>
    public static string DescribeClashLikelihood(BigInteger valueSpace)
    {
        if (valueSpace >= 1_000_000_000)
            return "clashes draw again";

        var threshold = (long)Math.Sqrt((double)valueSpace);
        return $"clashes become likely past ~{threshold.ToString("N0", CultureInfo.InvariantCulture)} objects";
    }

    /// <summary>
    /// The full hint line for one item of the Random token's Format menu: the value space and the clash
    /// likelihood, for example "lower case · 4.3 billion values · clashes draw again" (Hex) or "1 million
    /// values · clashes become likely past ~1,000 objects" (Digits). Returns an empty string for a length that
    /// is not yet a valid positive number, so the menu shows no hint rather than a nonsense one while the
    /// administrator is still typing.
    /// </summary>
    public static string GetRandomFormatMenuHint(GeneratedValueRandomFormat format, int? length)
    {
        switch (format)
        {
            case GeneratedValueRandomFormat.Guid:
                return "36 characters · no clash checks needed";

            case GeneratedValueRandomFormat.Hex when length is > 0:
            {
                var space = BigInteger.Pow(16, length.Value);
                return $"lower case · {FormatApproximateCount(space)} values · {DescribeClashLikelihood(space)}";
            }

            case GeneratedValueRandomFormat.Digits when length is > 0:
            {
                var space = BigInteger.Pow(10, length.Value);
                return $"{FormatApproximateCount(space)} values · {DescribeClashLikelihood(space)}";
            }

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Whether a target attribute is one <see cref="SyncRuleMappingGenerationValidator"/> rule 1 and rule 2 would
    /// reject outright: multi-valued, or neither Text nor a Number target. Used to disable the target picker's
    /// options for a generated mapping; the reason is <see cref="GetGeneratedTargetDisabledReason"/>.
    /// </summary>
    public static bool IsGeneratedTargetDisabled(AttributeDataType type, AttributePlurality plurality)
    {
        if (plurality == AttributePlurality.MultiValued)
            return true;

        return type != AttributeDataType.Text && !IsNumberTarget(type);
    }

    /// <summary>
    /// The reason a target is unavailable to a generated mapping, matching <see cref="IsGeneratedTargetDisabled"/>.
    /// Null when the target is available.
    /// </summary>
    public static string? GetGeneratedTargetDisabledReason(AttributeDataType type, AttributePlurality plurality)
    {
        if (plurality == AttributePlurality.MultiValued)
            return "A generated value can only be assigned to a single-valued attribute.";

        if (type != AttributeDataType.Text && !IsNumberTarget(type))
            return $"A generated value can only target a Text or Number attribute; \"{type}\" is not supported.";

        return null;
    }
}
