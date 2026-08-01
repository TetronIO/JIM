// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// One preview run: what was proposed, how far each stage got, and the decisions taken about how much of the result
/// to keep. One row per preview Activity, which owns it; the Activity carries the progress, status and initiator, so
/// nothing here duplicates them.
///
/// **Stage progress must also be written to the Activity, not only here.** The `trg_activities_notify_progress`
/// trigger watches `Activities.Status`, `Message`, `ObjectsProcessed` and `ObjectsToProcess`; an orchestrator that
/// recorded stage transitions on this row alone would raise no notification and leave the preview panel silent.
/// </summary>
public class ConfigurationChangePreview
{
    /// <summary>
    /// The preview's Activity, which is also this row's primary key: a preview and its Activity are one thing seen
    /// two ways, and the shared key means preview rows are removed by the Activity's own retention housekeeping.
    /// </summary>
    public Guid ActivityId { get; set; }

    public Activity Activity { get; set; } = null!;

    /// <summary>The configuration surface previewed; selects the adapter that produced the result.</summary>
    public ConfigurationChangePreviewSurface Surface { get; set; } = ConfigurationChangePreviewSurface.NotSet;

    /// <summary>
    /// The proposed configuration as submitted, serialised the same way <see cref="Activity.ConfigurationChangeSnapshot"/>
    /// is. Kept so a preview can be read back and explained after the proposal has been applied, abandoned, or
    /// superseded; without it a stored result is a set of numbers about a configuration nobody can reconstruct.
    /// </summary>
    public string? ProposedConfigurationSnapshot { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Stage state. Explicit per-stage columns rather than a child table: the panel reads this row on every refresh,
    // and a preview has exactly four stages whose meanings are fixed by the adapter contract.
    // -----------------------------------------------------------------------------------------------------------------

    public ConfigurationChangePreviewStageStatus ValidationStatus { get; set; } = ConfigurationChangePreviewStageStatus.NotStarted;
    public DateTime? ValidationStarted { get; set; }
    public DateTime? ValidationCompleted { get; set; }

    public ConfigurationChangePreviewStageStatus ImpactCountsStatus { get; set; } = ConfigurationChangePreviewStageStatus.NotStarted;
    public DateTime? ImpactCountsStarted { get; set; }
    public DateTime? ImpactCountsCompleted { get; set; }

    public ConfigurationChangePreviewStageStatus SummaryStatus { get; set; } = ConfigurationChangePreviewStageStatus.NotStarted;
    public DateTime? SummaryStarted { get; set; }
    public DateTime? SummaryCompleted { get; set; }

    public ConfigurationChangePreviewStageStatus DeltasStatus { get; set; } = ConfigurationChangePreviewStageStatus.NotStarted;
    public DateTime? DeltasStarted { get; set; }
    public DateTime? DeltasCompleted { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Cost, capping and staleness
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The affected population the adapter estimated before evaluation began. Drives the dispatch decision
    /// (in-process versus JIM.Worker) and the cap recommendation, and is recorded so the dispatch threshold can be
    /// tuned later against what previews actually cost rather than against a guess.
    /// </summary>
    public int EstimatedAffectedObjects { get; set; }

    /// <summary>Estimated delta rows, from the affected population and the adapter's deltas-per-object constant.</summary>
    public long EstimatedDeltaRows { get; set; }

    /// <summary>Whether delta rows were kept in full or capped per group. Group counts are exact either way.</summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } = ConfigurationChangePreviewDeltaPersistence.Full;

    /// <summary>
    /// True when the preview ran in JIM.Worker rather than in JIM.Web's process. Recorded for diagnostics only: the
    /// two paths run the same orchestration and write the same rows, and the panel does not distinguish them.
    /// </summary>
    public bool DispatchedToWorker { get; set; }

    /// <summary>
    /// The most recent import or synchronisation across the Connected Systems the preview depends on, sampled when
    /// the preview was generated. A later import moves the real answer; comparing this to the current value is how
    /// the panel knows to label a result stale rather than presenting it as current.
    /// </summary>
    public DateTime? StalenessBaseline { get; set; }

    public ICollection<ConfigurationChangePreviewGroup> Groups { get; set; } = [];

    public ICollection<ConfigurationChangePreviewDelta> Deltas { get; set; } = [];

    /// <summary>
    /// True when every stage that was going to run has finished successfully. A preview with any failed stage is not
    /// complete, however much of it produced results.
    /// </summary>
    public bool IsComplete =>
        IsStageSettled(ValidationStatus) && IsStageSettled(ImpactCountsStatus) &&
        IsStageSettled(SummaryStatus) && IsStageSettled(DeltasStatus);

    /// <summary>True when any stage failed, in which case no part of the result should be read as authoritative.</summary>
    public bool HasFailed =>
        ValidationStatus == ConfigurationChangePreviewStageStatus.Failed ||
        ImpactCountsStatus == ConfigurationChangePreviewStageStatus.Failed ||
        SummaryStatus == ConfigurationChangePreviewStageStatus.Failed ||
        DeltasStatus == ConfigurationChangePreviewStageStatus.Failed;

    private static bool IsStageSettled(ConfigurationChangePreviewStageStatus status) =>
        status is ConfigurationChangePreviewStageStatus.Complete or ConfigurationChangePreviewStageStatus.NotApplicable;
}
