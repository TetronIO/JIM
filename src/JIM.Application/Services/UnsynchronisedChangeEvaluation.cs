// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Core;
using JIM.Models.Sync;
using JIM.Utilities;

namespace JIM.Application.Services;

/// <summary>
/// Turns what a write outside synchronisation recorded on a Metaverse Object
/// (<see cref="MetaverseObject.UnsynchronisedChangesJson"/>) into the inputs export evaluation takes, so the review
/// that delivers the change stages it exactly as a synchronisation-driven change would (#2615).
/// </summary>
public static class UnsynchronisedChangeEvaluation
{
    /// <summary>
    /// Reads what a Metaverse Object records as changed outside synchronisation, against the object as it stands, for
    /// the review (or its preview) to evaluate with. An object that records nothing, or whose record cannot be read, is
    /// reviewed for scope alone: failing the run over a record would fail it for a person it is not even about.
    /// </summary>
    /// <param name="mvo">The Metaverse Object, with its attribute values loaded.</param>
    /// <param name="existingAttributeIds">The ids of the Metaverse Attributes that exist; see <see cref="Build"/>.</param>
    public static UnsynchronisedChangeEvaluationInput Read(MetaverseObject mvo, IReadOnlyCollection<int> existingAttributeIds)
    {
        ArgumentNullException.ThrowIfNull(mvo);

        MetaverseObjectUnsynchronisedChanges? changes;
        try
        {
            changes = MetaverseObjectUnsynchronisedChanges.FromJson(mvo.UnsynchronisedChangesJson);
        }
        catch (JsonException)
        {
            return UnsynchronisedChangeEvaluationInput.None with { RecordUnreadable = true };
        }

        return changes == null ? UnsynchronisedChangeEvaluationInput.None : Build(mvo, changes, existingAttributeIds);
    }

    /// <summary>
    /// Reads the changes against the object as it stands when the review runs.
    /// <para>
    /// The changed values are the object's current values for every recorded attribute: the additions are not stored,
    /// the object holds them. The removed values are the recorded ones the object does not hold again, so a value
    /// removed and then given back before the synchronisation is never removed, and the order of the edits never
    /// matters. They are unsaved values with no id, which export staging matches by the referenced object and then by
    /// content, and they follow the current values in the changed list, as removals follow additions in a
    /// synchronisation's own.
    /// </para>
    /// </summary>
    /// <param name="mvo">The Metaverse Object, with its attribute values loaded.</param>
    /// <param name="changes">What was recorded as changed.</param>
    /// <param name="existingAttributeIds">The ids of the Metaverse Attributes that exist. A recorded attribute that does
    /// not (it was deleted since) is skipped and reported, rather than failing the review of the object.</param>
    public static UnsynchronisedChangeEvaluationInput Build(
        MetaverseObject mvo,
        MetaverseObjectUnsynchronisedChanges changes,
        IReadOnlyCollection<int> existingAttributeIds)
    {
        ArgumentNullException.ThrowIfNull(mvo);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(existingAttributeIds);

        var recordedIds = changes.AttributeIds.Concat(changes.Removals.Select(removal => removal.AttributeId)).Distinct().ToList();
        var skipped = recordedIds.Where(id => !existingAttributeIds.Contains(id)).ToList();
        var attributeIds = recordedIds.Except(skipped).ToHashSet();

        var current = mvo.AttributeValues.Where(value => attributeIds.Contains(value.AttributeId)).ToList();

        // Asserted-null markers (#91) hold no value, so they hold nothing a removal could be given back by.
        var held = current.Where(value => !value.NullValue).Select(KeyOf).ToHashSet();
        var removed = changes.Removals
            .Where(removal => attributeIds.Contains(removal.AttributeId) && removal.HasContent)
            .Where(removal => !held.Contains(KeyOf(removal)))
            .Select(removal => removal.ToAttributeValue())
            .ToList();

        return new UnsynchronisedChangeEvaluationInput(
            [.. current, .. removed],
            removed.Count > 0 ? removed.ToHashSet() : null,
            skipped);
    }

    // What export staging matches a value by when it has no id: the referenced object, else its content. The attribute
    // is part of the key because the same text can be held under two attributes.
    private static (int, string?, int?, long?, decimal?, Guid?, bool?, long?, Guid?) KeyOf(MetaverseObjectAttributeValue value) =>
        (value.AttributeId, value.StringValue, value.IntValue, value.LongValue, value.DecimalValue, value.GuidValue, value.BoolValue,
            value.DateTimeValue.HasValue ? DateTimeAttributeValue.Key(value.DateTimeValue.Value) : null,
            value.ReferenceValue?.Id ?? value.ReferenceValueId);

    private static (int, string?, int?, long?, decimal?, Guid?, bool?, long?, Guid?) KeyOf(MetaverseObjectUnsynchronisedRemoval removal) =>
        (removal.AttributeId, removal.StringValue, removal.IntValue, removal.LongValue, removal.DecimalValue, removal.GuidValue, removal.BoolValue,
            removal.DateTimeValue.HasValue ? DateTimeAttributeValue.Key(removal.DateTimeValue.Value) : null,
            removal.ReferenceValueId);
}
