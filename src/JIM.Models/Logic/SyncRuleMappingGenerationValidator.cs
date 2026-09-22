// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;

namespace JIM.Models.Logic;

/// <summary>
/// Validates a generated mapping's <see cref="SyncRuleMapping.Generation"/> settings (Unique Value Generation,
/// #242). Returns one complete, en-GB sentence per problem found, written for an administrator: the portal, the
/// REST API and PowerShell all return these messages verbatim (Phase 3), so each one names the rule that was
/// broken and, where it helps, the fix.
/// <para>
/// Whether an excluded Connected System actually participates in the flow (has an export Attribute Flow
/// targeting the attribute) is a server-side check against the wider configuration, not something this validator,
/// which only ever sees one mapping, can answer; that check belongs to <c>ConnectedSystemServer</c> (Phase 3).
/// </para>
/// </summary>
public static class SyncRuleMappingGenerationValidator
{
    /// <summary>
    /// The widest decimal value an <see cref="AttributeDataType.Number"/> (32-bit) attribute can hold, less one
    /// digit of headroom, so a Digits random token on a Number target can never overflow it.
    /// </summary>
    private const int MaxNumberRandomDigits = 9;

    /// <summary>
    /// The widest decimal value an <see cref="AttributeDataType.LongNumber"/> (64-bit) attribute can hold, less
    /// one digit of headroom.
    /// </summary>
    private const int MaxLongNumberRandomDigits = 18;

    private const int MaxSeparatorLength = 3;
    private const int MaxLetterSuffixStart = 26;
    private const int MaxFixedWidth = 18;
    private const int MaxRandomLength = 64;
    private const int MaxAttemptLimit = 100000;

    /// <summary>
    /// Validates <paramref name="mapping"/>'s Generation settings. Returns an empty list when the mapping is not
    /// a generated mapping (<see cref="SyncRuleMapping.Generation"/> is null), or when every setting is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(SyncRuleMapping mapping)
    {
        var generation = mapping.Generation;
        if (generation == null)
            return [];

        var errors = new List<string>();

        var targetType = mapping.TargetMetaverseAttribute?.Type ?? mapping.TargetConnectedSystemAttribute?.Type;
        var targetPlurality = mapping.TargetMetaverseAttribute?.AttributePlurality ?? mapping.TargetConnectedSystemAttribute?.AttributePlurality;
        var isNumberTarget = targetType is AttributeDataType.Number or AttributeDataType.LongNumber;

        // Rule 1: the target must be single-valued.
        if (targetPlurality == AttributePlurality.MultiValued)
            errors.Add("A generated value can only be assigned to a single-valued attribute; choose a single-valued target, or a different source type.");

        // Rule 2: the target type must be Text, or a Number target (Number or Long Number).
        if (targetType.HasValue && targetType != AttributeDataType.Text && !isNumberTarget)
            errors.Add($"A generated value can only target a Text or Number attribute; \"{targetType.Value}\" is not supported.");

        // Rule 3: sources. A generated mapping's only permitted source is one expression; only-if-taken requires it.
        var expressionSourceCount = mapping.Sources.Count(s => !string.IsNullOrWhiteSpace(s.Expression));
        var nonExpressionSourceCount = mapping.Sources.Count - expressionSourceCount;

        if (nonExpressionSourceCount > 0)
            errors.Add("A generated mapping's only permitted source is a single base expression; remove any attribute sources.");
        else if (mapping.Sources.Count > 1)
            errors.Add("A generated mapping can have at most one source, the base expression; remove the additional sources.");
        else if (generation.TokenKind == GeneratedValueTokenKind.OnlyIfTaken && expressionSourceCount == 0)
            errors.Add("\"Only if taken\" requires a base expression; add one, or choose a Sequence or Random token instead.");

        // Rule 4: Number target constraints.
        if (isNumberTarget)
        {
            if (generation.TokenKind == GeneratedValueTokenKind.OnlyIfTaken)
                errors.Add("A Number target cannot use \"Only if taken\"; choose a Sequence or a Random token with the Digits format instead.");
            else if (generation.TokenKind == GeneratedValueTokenKind.Random && generation.RandomFormat != GeneratedValueRandomFormat.Digits)
                errors.Add("A Number target's Random token must use the Digits format; a GUID or hexadecimal value is text.");

            if (expressionSourceCount > 0)
                errors.Add("A Number target cannot have a base expression; a prefix or letters would make the generated value text.");

            if (generation.FixedWidth.HasValue)
                errors.Add("A Number target cannot be padded to a fixed width; a Number attribute cannot hold leading zeros.");

            if (!string.IsNullOrEmpty(generation.Separator))
                errors.Add("A Number target cannot have a separator; there is no base value for it to separate.");

            if (generation.TokenKind == GeneratedValueTokenKind.Random
                && generation.RandomFormat == GeneratedValueRandomFormat.Digits
                && generation.RandomLength.HasValue)
            {
                var maxDigits = targetType == AttributeDataType.LongNumber ? MaxLongNumberRandomDigits : MaxNumberRandomDigits;
                if (generation.RandomLength.Value > maxDigits)
                {
                    var targetLabel = targetType == AttributeDataType.LongNumber ? "Long Number" : "Number";
                    errors.Add($"A Digits random token on a {targetLabel} target can have at most {maxDigits} digits; reduce the length, or use a Text target.");
                }
            }
        }

        // Rule 5: only-if-taken suffix settings.
        if (generation.TokenKind == GeneratedValueTokenKind.OnlyIfTaken)
        {
            if (generation.SuffixStart < 1)
                errors.Add("The collision suffix must start at 1 or higher.");
            else if (generation.SuffixStyle == GeneratedValueSuffixStyle.Letter && generation.SuffixStart > MaxLetterSuffixStart)
                errors.Add("A letter suffix can start no higher than 26 (\"z\"); use a Number suffix for a higher starting value.");
        }

        // Rule 6: sequence settings.
        if (generation.TokenKind == GeneratedValueTokenKind.Sequence)
        {
            if (generation.SequenceStart < 0)
                errors.Add("The sequence start value must be 0 or higher.");

            if (generation.SequenceIncrement < 1)
                errors.Add("The sequence increment must be 1 or higher.");

            if (generation.FixedWidth.HasValue)
            {
                if (generation.FixedWidth.Value is < 1 or > MaxFixedWidth)
                    errors.Add($"The fixed width must be between 1 and {MaxFixedWidth} digits.");
                else
                {
                    var startDigits = Math.Abs(generation.SequenceStart).ToString(CultureInfo.InvariantCulture).Length;
                    if (generation.FixedWidth.Value < startDigits)
                        errors.Add($"The fixed width ({generation.FixedWidth.Value}) must be at least as wide as the sequence start value ({generation.SequenceStart}, {startDigits} digits).");
                }
            }

            if (!generation.NeverReuse)
                errors.Add("A sequence token cannot have \"Never reuse a value\" turned off; sequence numbers are never reused.");
        }

        // Rule 7: random settings.
        if (generation.TokenKind == GeneratedValueTokenKind.Random)
        {
            switch (generation.RandomFormat)
            {
                case GeneratedValueRandomFormat.Guid:
                    if (generation.RandomLength.HasValue)
                        errors.Add("A GUID random token has a fixed length; remove the configured length.");
                    break;

                case GeneratedValueRandomFormat.Hex:
                case GeneratedValueRandomFormat.Digits:
                    if (!generation.RandomLength.HasValue || generation.RandomLength.Value is < 1 or > MaxRandomLength)
                    {
                        var formatLabel = generation.RandomFormat == GeneratedValueRandomFormat.Hex ? "hexadecimal" : "digit";
                        errors.Add($"A {formatLabel} random token needs a length between 1 and {MaxRandomLength} characters.");
                    }
                    break;
            }
        }

        // Rule 8: separator. Only meaningful (and only offered) alongside a base expression; the Number-target
        // case is covered by rule 4 above, to avoid reporting the same misconfiguration twice.
        if (!isNumberTarget && generation.Separator != null)
        {
            if (generation.Separator.Length is < 1 or > MaxSeparatorLength)
                errors.Add("The separator must be 1 to 3 characters.");
            else if (generation.Separator.Any(char.IsWhiteSpace))
                errors.Add("The separator cannot contain whitespace.");
            else if (generation.Separator.Contains('@'))
                errors.Add("The separator cannot contain \"@\".");

            if (expressionSourceCount == 0)
                errors.Add("A separator is only meaningful alongside a base expression; remove the separator, or add a base expression.");
        }

        // Rule 9: attempt limit.
        if (generation.AttemptLimit is < 1 or > MaxAttemptLimit)
            errors.Add($"The attempt limit must be between 1 and {MaxAttemptLimit}.");

        // Rule 10: exclusions must not name the same Connected System twice.
        var hasDuplicateExclusion = generation.Exclusions
            .GroupBy(e => e.ConnectedSystemId)
            .Any(g => g.Count() > 1);
        if (hasDuplicateExclusion)
            errors.Add("A Connected System cannot be excluded more than once from the same generated mapping.");

        return errors;
    }
}
