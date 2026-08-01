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
public enum ConfigurationChangePreviewSurface
{
    /// <summary>Uninitialised. Never valid on a persisted preview.</summary>
    NotSet = 0,

    /// <summary>
    /// A Synchronisation Rule: its scope, its Attribute Flow, and the Deprovisioning and Out-of-Scope Actions that
    /// decide what happens to objects the rule stops covering (#827 gaps G1, G2 and G3).
    /// </summary>
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
    MetaverseAttribute = 4
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
    NotApplicable = 4
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
