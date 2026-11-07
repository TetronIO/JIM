// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// How Pending Export reconciliation reads one page's values (#2669): the attribute values it asks the database about
/// rather than loads, one question per Connected System Object and attribute. Every other value is loaded as before.
/// </summary>
public sealed class ReconciliationProbePlan
{
    /// <summary>
    /// The questions to ask: at most one per Connected System Object and attribute.
    /// </summary>
    public required IReadOnlyList<ReconciliationProbeQuestion> Questions { get; init; }

    /// <summary>
    /// How many multi-valued reference attributes with changes awaiting confirmation could not be put as a question, and
    /// are loaded in full instead: for diagnostics.
    /// </summary>
    public required int FullLoadPairCount { get; init; }

    /// <summary>
    /// <see cref="FullLoadPairCount"/> broken down by why each pair could not be put as a question: for diagnostics.
    /// </summary>
    public IReadOnlyDictionary<ReconciliationProbeFallbackReason, int> FullLoadPairCountsByReason { get; init; } =
        new Dictionary<ReconciliationProbeFallbackReason, int>();
}
