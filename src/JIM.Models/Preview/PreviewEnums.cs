// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// A configuration surface a preview can be generated for: one adapter per value. Persisted by ordinal, so append
/// only; adding a value also needs an entry in <see cref="ConfigurationChangePreviewSurfaces"/>, which the model
/// tests enforce.
///
/// Deliberately narrower than <see cref="Activities.ActivityTargetType"/>: that enum covers everything JIM records
/// an Activity for, including operational work no adapter could preview, and a registry keyed on it would accept
/// keys that can never resolve.
/// </summary>
/// <remarks>
/// A surface is a KIND OF CHANGE, not an entity: one entity's settings can be several surfaces, because each is
/// evaluated by a different adapter and exactly one adapter may serve a surface. A Synchronisation Rule alone has
/// three (its Scoping Criteria, its Attribute Flow, and its deprovisioning actions), and they answer different
/// questions from different data. Several surfaces therefore map to one <see cref="ActivityTargetType"/>, which is
/// correct: the target type says which object a preview was about, and the surface says what about it.
/// </remarks>
public enum ConfigurationChangePreviewSurface
{
    /// <summary>Uninitialised. Never valid on a persisted preview.</summary>
    NotSet = 0,

    /// <summary>
    /// A Synchronisation Rule's Deprovisioning and Out-of-Scope Actions: what happens to objects the rule stops
    /// covering (#827 gap G3).
    /// </summary>
    /// <remarks>
    /// Named for the entity rather than the change because it was the first of the rule's surfaces to get an
    /// adapter, and the name is on the REST wire (enums serialise by name, integers refused), so it is left alone
    /// rather than renamed for tidiness. See the note on the enum itself about surfaces being change kinds.
    /// </remarks>
    SynchronisationRule = 1,

    /// <summary>
    /// A Connected System: schema selection, and the partitions and containers it imports from (#827 gap G4).
    /// </summary>
    ConnectedSystem = 2,

    /// <summary>
    /// A Metaverse Object Type's deletion settings: the rule, its grace period, and its trigger systems (#827 gap
    /// G5). The pilot surface, and the one whose changes take effect without a synchronisation run in between.
    /// </summary>
    MetaverseObjectType = 3,

    /// <summary>
    /// A Metaverse Attribute: its data type, plurality, and which Metaverse Object Types it is bound to.
    /// </summary>
    MetaverseAttribute = 4,

    /// <summary>
    /// A Synchronisation Rule's Scoping Criteria: which objects the rule manages at all (#827 gap G1).
    /// </summary>
    SynchronisationRuleScope = 5,

    /// <summary>
    /// A Synchronisation Rule's Attribute Flow mappings: what the objects it manages would have written to them
    /// (#827 gap G2).
    /// </summary>
    SynchronisationRuleAttributeFlow = 6
}

/// <summary>
/// The four stages of a preview, in the order they run. Each completes independently and its results are shown as
/// soon as they land, so an administrator is never left watching a spinner with nothing to read.
/// </summary>
public enum ConfigurationChangePreviewStage
{
    /// <summary>
    /// Structural findings: what about the proposed configuration is invalid, contradictory, or blocked. Runs
    /// synchronously in the request path and is near-instant.
    /// </summary>
    Validation = 0,

    /// <summary>Per-transition-type counts of the affected population, from set-based SQL only.</summary>
    ImpactCounts = 1,

    /// <summary>Exact summary groups computed from the evaluated delta stream.</summary>
    Summary = 2,

    /// <summary>Per-object deltas, persisted in full or capped per group.</summary>
    Deltas = 3
}

/// <summary>
/// How far one stage of a preview has got. A stage that failed never presents its partial results as complete: the
/// whole preview is failed, because a summary computed from a truncated evaluation is a wrong answer stated
/// confidently, which is worse than no answer.
/// </summary>
public enum ConfigurationChangePreviewStageStatus
{
    NotStarted = 0,
    InProgress = 1,
    Complete = 2,
    Failed = 3,

    /// <summary>
    /// The adapter does not implement this stage. A count-only adapter skips the delta stages, and skipping is a
    /// legitimate end state, not a failure or an omission.
    /// </summary>
    NotApplicable = 4,

    /// <summary>
    /// The administrator cancelled the preview while this stage was running. Distinct from
    /// <see cref="Failed"/> on purpose: nothing went wrong, and a cancelled stage shown as failed would send
    /// somebody looking for an error that was never raised.
    /// </summary>
    Cancelled = 5
}

/// <summary>
/// How much of the evaluated delta stream was persisted. Evaluation always processes the full population and group
/// counts are always exact; this records only what was kept for drill-down.
/// </summary>
public enum ConfigurationChangePreviewDeltaPersistence
{
    /// <summary>Every delta persisted; every drill-down list is complete.</summary>
    Full = 0,

    /// <summary>
    /// Capped per summary group, at the administrator's choice or because the estimate crossed the recommendation
    /// threshold. Groups whose rows were truncated are flagged and their drill-downs labelled sampled.
    /// </summary>
    Capped = 1
}

/// <summary>
/// How much a validation finding matters. Only <see cref="Blocking"/> stops the change being applied; the rest are
/// there to be read.
/// </summary>
public enum PreviewValidationSeverity
{
    Information = 0,
    Warning = 1,

    /// <summary>
    /// The proposed configuration cannot be applied at all. The apply path re-checks these rather than trusting the
    /// preview, so a stale preview can never wave through a change that has since become invalid.
    /// </summary>
    Blocking = 2
}
