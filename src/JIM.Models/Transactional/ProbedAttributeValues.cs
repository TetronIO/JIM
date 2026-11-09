// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// What Pending Export reconciliation was given for one attribute of one Connected System Object whose values were
/// probed rather than loaded (#2669): the answer to a <see cref="ReconciliationProbeQuestion"/>. It says exactly what it
/// can answer, so the engine never mistakes it for the attribute's whole value set: whether a value of one of the
/// changes in <see cref="ProbedChangeIds"/> is present, and, when <see cref="CappedValueCount"/> is set, whether the
/// attribute holds none, one, or more than one value.
/// </summary>
public sealed class ProbedAttributeValues
{
    public required Guid ConnectedSystemObjectId { get; init; }

    public required int AttributeId { get; init; }

    /// <summary>
    /// The changes the probe searched for. Reconciliation fails rather than judge any other change from this answer.
    /// </summary>
    public required IReadOnlySet<Guid> ProbedChangeIds { get; init; }

    /// <summary>
    /// Every stored row that could match one of <see cref="ProbedChangeIds"/>, possibly with others: a superset, which the
    /// engine judges with the same comparison it applies to a full load. Each carries its id, object, attribute id,
    /// <c>ReferenceValueId</c> and <c>UnresolvedReferenceValue</c>, but no attribute definition (the engine compares by the
    /// changes' attribute); nothing else is read.
    /// </summary>
    public required IReadOnlyList<ConnectedSystemObjectAttributeValue> CandidateValues { get; init; }

    /// <summary>
    /// How many values the attribute holds, capped at two (0, 1, or 2 for two or more), or null when no change asked.
    /// </summary>
    public int? CappedValueCount { get; init; }
}
