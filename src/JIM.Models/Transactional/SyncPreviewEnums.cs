// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What a preview message is about (#288, PRD requirement 16): the machine-readable code that lets a consumer
/// distinguish conditions programmatically rather than by string parsing. Whether a message blocks the sync is
/// carried by which list it sits in on <see cref="SyncPreviewResult"/> (Errors block, Warnings advise).
/// </summary>
public enum SyncPreviewMessageCode
{
    /// <summary>An expression-based Attribute Flow mapping failed to evaluate; the real sync would record an
    /// ExpressionEvaluationError against the object.</summary>
    ExpressionEvaluationError,
    /// <summary>A multi-valued source attribute flows to a single-valued target and holds more than one value;
    /// the real sync would refuse to pick one and record an error for the attribute (#435).</summary>
    MultiValuedToSingleValuedFlow,
    /// <summary>A staged change carries a reference that is not yet resolvable in the target system; the real
    /// export would be deferred until the referenced object is provisioned.</summary>
    UnresolvedReference,
    /// <summary>The Metaverse Object's one object in the target Connected System is of a different Connected
    /// System Object Type than the rule targets (#1331); the rule's export would not be staged.</summary>
    ObjectTypeConflict,
    /// <summary>The object is not in scope for any applicable Synchronisation Rule, so the chain stops here.</summary>
    OutOfScope,
    /// <summary>No enabled Synchronisation Rule applies to the object at this step of the chain.</summary>
    NoApplicableSyncRule,
    /// <summary>The object the preview was asked about does not exist (an expected block: the preview returns
    /// with this error rather than throwing, per PRD requirement 5).</summary>
    ObjectNotFound,
    /// <summary>More than one Metaverse Object matched the Object Matching Rules; the real sync would fail the
    /// object with an AmbiguousMatch error.</summary>
    AmbiguousMatch
}

/// <summary>
/// The outcome category a full-system preview classifies each evaluated object into (#288, PRD decision D2):
/// the count tier counts every object per category, and the sample tier retains a bounded number of full
/// trees per category.
/// </summary>
public enum FullSyncPreviewCategory
{
    /// <summary>The object would project a new Metaverse Object.</summary>
    WouldProject,
    /// <summary>The object would join an existing Metaverse Object via Object Matching Rules.</summary>
    WouldJoin,
    /// <summary>The object is already joined; the preview evaluated its Attribute Flows and outbound chain.</summary>
    AttributeFlow,
    /// <summary>The object is out of scope of every applicable import Synchronisation Rule with Scoping Criteria.</summary>
    OutOfScope,
    /// <summary>No rule would connect the object: nothing matched and nothing projects.</summary>
    NotConnected,
    /// <summary>The preview surfaced at least one blocking error for the object.</summary>
    BlockedByErrors
}

/// <summary>
/// Why a full-system preview stopped before evaluating the whole population (#288, PRD requirement 14).
/// </summary>
public enum FullSyncPreviewTruncationReason
{
    /// <summary>The whole population was evaluated.</summary>
    None,
    /// <summary>The object cap in <see cref="FullSyncPreviewOptions.MaxObjects"/> was reached.</summary>
    ObjectCapReached,
    /// <summary>The time budget in <see cref="FullSyncPreviewOptions.TimeBudget"/> ran out.</summary>
    TimeBudgetExhausted
}
