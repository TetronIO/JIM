// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Staging;

/// <summary>
/// The database's answer to a <see cref="DriftHeldReferenceQuestion"/> (#2818): everything Drift Detection reads of a
/// multi-valued reference attribute it compares as a set, without the values the two sides agree on. A held value stands
/// for the Metaverse Object its referenced Connected System Object is joined to; a value that resolves to none (an
/// unresolved reference, a reference to an object not joined, a value held under another data type) is not one, exactly
/// as Drift Detection reads the attribute when it is loaded whole.
/// </summary>
public sealed class DriftHeldReferenceAnswer
{
    public DriftHeldReferenceAnswer(DriftHeldReferenceQuestion question)
    {
        Question = question;
    }

    /// <summary>
    /// The question answered.
    /// </summary>
    public DriftHeldReferenceQuestion Question { get; }

    /// <summary>
    /// The Metaverse Objects the held values resolve to that are not expected, each once, in the order of the first held
    /// value resolving to each (value id order, the order the object's values are loaded in).
    /// </summary>
    public List<Guid> HeldNotExpected { get; } = [];

    /// <summary>
    /// The expected Metaverse Objects no held value resolves to.
    /// </summary>
    public HashSet<Guid> ExpectedNotHeld { get; } = [];

    /// <summary>
    /// How many values the object holds of the attribute, whatever they hold.
    /// </summary>
    public int HeldValueCount { get; set; }

    /// <summary>
    /// Whether any held value holds a reference, resolved or not.
    /// </summary>
    public bool HoldsReference { get; set; }

    /// <summary>
    /// The data type of the first held value (in value id order) that holds something other than a reference; null when
    /// none does.
    /// </summary>
    public AttributeDataType? FirstOtherHeldDataType { get; set; }

    /// <summary>
    /// The data type the attribute is held under when it is held only under another data type (#2319): no value holds a
    /// reference and one holds something else. Drift Detection does not compare such an attribute. Null otherwise.
    /// </summary>
    public AttributeDataType? HeldOnlyUnderAnotherDataType => HoldsReference ? null : FirstOtherHeldDataType;

    /// <summary>
    /// Whether the held and expected sets differ.
    /// </summary>
    public bool Differs => HeldNotExpected.Count > 0 || ExpectedNotHeld.Count > 0;

    /// <summary>
    /// The answer worked out from the values themselves, as the database works it out: the reference against which the
    /// database's statement is tested, and how the in-memory repository answers.
    /// </summary>
    /// <param name="question">The question.</param>
    /// <param name="heldValues">Every value the object holds of the attribute, in value id order.</param>
    /// <param name="resolveMetaverseObjectId">The Metaverse Object a held value resolves to, or null.</param>
    public static DriftHeldReferenceAnswer Compute(DriftHeldReferenceQuestion question,
        IEnumerable<ConnectedSystemObjectAttributeValue> heldValues, Func<ConnectedSystemObjectAttributeValue, Guid?> resolveMetaverseObjectId)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(heldValues);
        ArgumentNullException.ThrowIfNull(resolveMetaverseObjectId);

        var answer = new DriftHeldReferenceAnswer(question);
        var expected = question.ExpectedValues.ToHashSet();
        var held = new HashSet<Guid>();
        foreach (var value in heldValues)
        {
            answer.HeldValueCount++;
            if (value.HoldsValueOfType(AttributeDataType.Reference))
                answer.HoldsReference = true;
            else
                answer.FirstOtherHeldDataType ??= value.HeldDataType();

            if (resolveMetaverseObjectId(value) is not { } metaverseObjectId || !held.Add(metaverseObjectId))
                continue;

            if (!expected.Contains(metaverseObjectId))
                answer.HeldNotExpected.Add(metaverseObjectId);
        }

        answer.ExpectedNotHeld.UnionWith(question.ExpectedValues.Where(value => !held.Contains(value)));
        return answer;
    }
}
