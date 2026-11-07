// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// One multi-valued reference attribute of one Connected System Object in an export batch that could be loaded in part
/// (#2816), as <see cref="ExportValueLoadPlanner.PlanCandidates"/> found it. It is loaded in part only when it can be
/// asked about (<see cref="Question"/> is set) and holds at least <see cref="Cap"/> values; otherwise it is loaded whole.
/// </summary>
/// <param name="ConnectedSystemObjectId">The object.</param>
/// <param name="Attribute">The attribute.</param>
/// <param name="Cap">How many values the attribute is counted up to: two more than the changes the batch makes to it,
/// so an attribute held at the cap cannot be emptied by them, and a count below it is exact.</param>
/// <param name="Question">The rows the batch's changes could match, asked of the database; null when a change cannot be
/// put that way.</param>
/// <param name="FallbackReason">Why the attribute cannot be loaded in part; null when it can.</param>
public sealed record ExportValueTrimCandidate(
    Guid ConnectedSystemObjectId,
    ConnectedSystemObjectTypeAttribute Attribute,
    int Cap,
    ReconciliationProbeQuestion? Question,
    ExportValueLoadFallbackReason? FallbackReason)
{
    /// <summary>
    /// Whether the attribute is loaded in part, given how many values it holds counted up to <see cref="Cap"/>.
    /// </summary>
    public bool IsTrimmedAt(int cappedValueCount) => Question != null && cappedValueCount >= Cap;
}
