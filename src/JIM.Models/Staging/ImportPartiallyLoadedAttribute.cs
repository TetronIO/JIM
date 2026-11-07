// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// An attribute a Delta Import loaded in part (#2817): a large group's member attribute holding only the values the
/// database's comparison left undecided, with the answer that settled the rest. The comparison uses the attribute only with
/// <see cref="Answer"/>.
/// </summary>
/// <param name="AttributeId">The attribute loaded in part.</param>
/// <param name="AttributeName">Its name.</param>
/// <param name="Answer">The database's answer the attribute was loaded by.</param>
public sealed record ImportPartiallyLoadedAttribute(int AttributeId, string AttributeName, HeldReferenceValueAnswer Answer)
    : PartiallyLoadedAttribute(AttributeId, AttributeName)
{
    /// <summary>
    /// Whether the object held any value of the attribute when it was loaded, from the answer exactly: it holds the values
    /// left undecided (<see cref="HeldReferenceValueAnswer.UnmatchedValueIds"/>), those related to an imported value, and at
    /// least one for every imported value a held value matched exactly; nothing else.
    /// </summary>
    public override bool HoldsAnyValue =>
        Answer.UnmatchedValueIds.Count > 0
        || Answer.RelatedValues.Count > 0
        || Answer.Question.Values.Count > Answer.UnmatchedIncomingValues.Count;

    /// <summary>
    /// Not known: the answer settles held values by matching them, not by counting them.
    /// </summary>
    public override int? AtLeastValueCount => null;

    /// <inheritdoc />
    public override string Describe() =>
        $"for a Delta Import's comparison, with the {Answer.UnmatchedValueIds.Count} value(s) the database left undecided";
}
