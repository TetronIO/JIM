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
    AmbiguousMatch,
    /// <summary>The Metaverse Object's deletion (cascading from this object falling out of scope) would
    /// disconnect a downstream joined Connected System Object without deprovisioning it: no export
    /// Synchronisation Rule matches it, or a matching rule's Outbound Deprovision Action is Disconnect. The
    /// real run records no outcome node for a disconnect-only downstream object (#288 Phase 1 of the Sync
    /// Preview Surface plan); this warning is how the preview still surfaces it.</summary>
    DownstreamDisconnectOnly,
    /// <summary>A generated Attribute Flow mapping would fail to resolve (the generation is exhausted, has no
    /// base value, or its width would be exceeded); the real sync would record a generated-value error against
    /// the object. Added at the end (#242, Phase 2 work package J):
    /// this enum is not persisted anywhere (no database column or migration references it, and the REST API
    /// serialises it by name via <c>JsonStringEnumConverter</c>), so member order carries no compatibility
    /// requirement, but new members are still appended for readability.</summary>
    GeneratedValueWouldFail,
    /// <summary>The enabled Attribute Flows that derive Metaverse attributes from other Metaverse attributes (#1750)
    /// contain a dependency cycle, so no evaluation order exists: the real synchronisation would refuse to start and
    /// process no object at all. The preview evaluates nothing either, and names the cycle.</summary>
    DerivedFlowCycle,
    /// <summary>An Object Matching Rule could not be worked out for the object (#2052); the real sync would neither
    /// join nor project it (or, matching before provisioning, neither join nor provision it), and would record an
    /// ObjectMatchingRuleFailed error naming the rule and the fault.</summary>
    ObjectMatchingRuleFailed,
    /// <summary>A Scoping Criterion could not be evaluated for the object (#2054): its expression failed, computed a
    /// value that cannot be compared as its type, or its pattern ran past its time limit. The real sync would record a
    /// ScopingCriterionFailed error and leave the object untouched, so the preview evaluates nothing further.</summary>
    ScopingCriterionFailed,
    /// <summary>Generated Value Forms (#2023): the generated value would be held back because a value built from it
    /// needs another input the object has no value for yet, so the candidate's values cannot be worked out and checked.
    /// Not a failure: the real synchronisation generates nothing for the attribute this time, and generates the value
    /// once the input arrives. The detail names each input and the value that needs it.</summary>
    GeneratedValueWaiting,
    /// <summary>Before provisioning, an Object Matching Rule found more than one existing Connected System Object in the
    /// target (#2052); the real sync would neither join the Metaverse Object to one nor provision it, and would record an
    /// AmbiguousProvisioningMatch error listing them.</summary>
    AmbiguousProvisioningMatch,
    /// <summary>Dates an export would send to a multi-valued attribute are one date at the step of time the Connected
    /// System holds it to (#2153); the real sync would send them as one and record a DatesCollapseAtTargetResolution
    /// item. Not a failure: the attribute still exports.</summary>
    DatesCollapseAtTargetResolution,
    /// <summary>An export Attribute Flow targets a credential attribute such as <c>password</c> (#2091); the real sync
    /// would stage nothing for it and record a CredentialAttributeNotExported item. The detail names the attribute.</summary>
    CredentialAttributeNotExported
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

/// <summary>
/// What one item of a streamed whole-system preview reports (#1530).
/// </summary>
public enum FullSyncPreviewItemKind
{
    /// <summary>The first item: the population about to be walked.</summary>
    Population,
    /// <summary>The synchronisation would refuse to run at all (a derived flow cycle); nothing else follows.</summary>
    Refused,
    /// <summary>A Connected System Object, evaluated as the synchronisation would process it.</summary>
    Evaluated,
    /// <summary>A Connected System Object awaiting deletion, which the synchronisation tears down rather than processes.</summary>
    Obsolete,
    /// <summary>A Metaverse Object flagged for an Export Review that no Connected System Object of this system reached.</summary>
    ExportScopeReview,
    /// <summary>The last item when a bound stopped the walk before the whole population was evaluated.</summary>
    Truncated
}
