// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Transactional;

namespace JIM.Application.UniqueValues;

/// <summary>
/// Writes a resolved <see cref="GenerationOutcome"/> value onto the <see cref="PendingExportAttributeValueChange"/>
/// it was resolved for, and clears its marker (Unique Value Generation, #242, Phase 2 work package H). Shared
/// by the worker's real resolve (<c>SyncTaskProcessorBase.ResolveExportGeneratedValuesAsync</c>) and Sync
/// Preview's dry-run resolve (<c>ExportEvaluationServer.ResolvePreviewGeneratedExportValuesAsync</c>), so the
/// checked Number conversion and the exact set of supported target types are defined once.
/// </summary>
public static class GeneratedExportValueWriter
{
    /// <summary>
    /// Sets <paramref name="textValue"/> or <paramref name="numericValue"/> onto the field
    /// <paramref name="change"/>'s target attribute type calls for, and clears
    /// <see cref="PendingExportAttributeValueChange.PendingGeneration"/> so the change reads as resolved.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A Number candidate does not fit a 32-bit Number attribute: a caller error (the mapping's target and the
    /// token settings that produced this number disagree), never a value to silently truncate.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="change"/>'s target attribute is a type a generated mapping cannot target (only Text,
    /// Number and LongNumber are valid; <c>SyncRuleMappingGenerationValidator</c> is what prevents this at
    /// configuration time).
    /// </exception>
    public static void Apply(PendingExportAttributeValueChange change, string? textValue, long? numericValue)
    {
        switch (change.Attribute.Type)
        {
            case AttributeDataType.Text:
                change.StringValue = textValue;
                break;

            case AttributeDataType.Number:
                var numberCandidate = numericValue!.Value;
                try
                {
                    change.IntValue = checked((int)numberCandidate);
                }
                catch (OverflowException ex)
                {
                    throw new InvalidOperationException(
                        $"Generated value {numberCandidate} for {change.Attribute.Name} does not fit a Number (32-bit) attribute. " +
                        "This indicates a sequence or random-digits token producing a value too wide for the mapping's target type.", ex);
                }
                break;

            case AttributeDataType.LongNumber:
                change.LongValue = numericValue;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Attribute.Type,
                    $"GeneratedExportValueWriter.Apply does not support target attribute type {change.Attribute.Type}.");
        }

        change.PendingGeneration = null;
    }
}
