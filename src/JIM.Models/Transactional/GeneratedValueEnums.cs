// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The lifecycle state of a <see cref="GeneratedValueAssignment"/> (Unique Value Generation, #242, plan
/// decision 17). Persisted by ordinal; append-only, in the style of <c>ActivityRunProfileExecutionItemErrorType</c>
/// and the other causality enums, so new members are added at the end and existing values are never renumbered.
/// </summary>
public enum GeneratedValueAssignmentState
{
    /// <summary>
    /// The value has been generated (or adopted) for this synchronisation run but not yet committed to the
    /// object; a placeholder held so incumbent detection sees the attribute as taken while the run completes.
    /// </summary>
    Proposed = 0,

    /// <summary>
    /// The value is settled: applied to the object (import mode) or accepted by the target (export mode). Sticky;
    /// never recomputed from the base expression.
    /// </summary>
    Committed = 1,

    /// <summary>
    /// The value was revised after an attributable, unanchored export rejection (Collision Remediation, release 4).
    /// The object is flagged for review; the next synchronisation re-stages the export.
    /// </summary>
    Remediated = 2,

    /// <summary>
    /// The value was rejected and could not be safely revised automatically (anchored elsewhere, unknown, or the
    /// remediation attempt limit was exhausted). Waits on an administrator decision (release 4).
    /// </summary>
    NeedsDecision = 3
}
