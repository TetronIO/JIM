// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// A configuration change preview as an API caller sees it: how far each stage got, what validation found, how many
/// objects each transition would affect, and the summary groups behind the numbers.
/// </summary>
public class ConfigurationChangePreviewResponse
{
    /// <summary>The preview's Activity, which is also its identifier.</summary>
    public Guid ActivityId { get; set; }

    /// <summary>The configuration surface previewed.</summary>
    public ConfigurationChangePreviewSurface Surface { get; set; }

    /// <summary>
    /// The Activity's own status. Read this before the stage statuses: a preview that failed, or was cancelled,
    /// says so here first.
    /// </summary>
    public ActivityStatus ActivityStatus { get; set; }

    /// <summary>What the preview is currently doing, or what it finished doing.</summary>
    public string? Message { get; set; }

    /// <summary>Why the preview failed, when it did. Never contains attribute values.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Delta rows evaluated so far, and the number expected; the preview's progress.</summary>
    public int ObjectsProcessed { get; set; }

    public int ObjectsToProcess { get; set; }

    public ConfigurationChangePreviewStageStatus ValidationStatus { get; set; }
    public ConfigurationChangePreviewStageStatus ImpactCountsStatus { get; set; }
    public ConfigurationChangePreviewStageStatus SummaryStatus { get; set; }
    public ConfigurationChangePreviewStageStatus DeltasStatus { get; set; }

    /// <summary>True when every stage that was going to run has finished successfully.</summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// True when any stage failed. Nothing in this response should be read as authoritative when it is: a preview
    /// that failed part-way has seen an arbitrary subset of the population.
    /// </summary>
    public bool HasFailed { get; set; }

    /// <summary>What stage 1 found about the proposal itself.</summary>
    public List<PreviewValidationFinding> ValidationFindings { get; set; } = [];

    /// <summary>
    /// Stage 2's per-transition counts, from set-based SQL over the whole population. Distinct from the summary
    /// groups below, which come from the evaluated delta stream; an adapter may produce one and not the other.
    /// </summary>
    public List<PreviewImpactCount> ImpactCounts { get; set; } = [];

    /// <summary>The summary groups, largest first. Counts here are exact even where drill-down rows were capped.</summary>
    public List<ConfigurationChangePreviewGroupResponse> Groups { get; set; } = [];

    /// <summary>The affected population the adapter estimated before evaluation began.</summary>
    public int EstimatedAffectedObjects { get; set; }

    public long EstimatedDeltaRows { get; set; }

    /// <summary>
    /// Whether drill-down rows were kept in full or capped per group. Group counts are exact either way; this says
    /// only how much can be drilled into.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; }

    /// <summary>Whether the evaluation ran in JIM.Worker. Diagnostic only; both paths produce identical results.</summary>
    public bool DispatchedToWorker { get; set; }

    /// <summary>
    /// The most recent import or synchronisation across the systems the preview depends on, sampled when it ran.
    /// A later import moves the real answer, which is how a caller knows to treat a result as stale.
    /// </summary>
    public DateTime? StalenessBaseline { get; set; }

    public static ConfigurationChangePreviewResponse FromEntity(ConfigurationChangePreview preview, Activity activity,
        IEnumerable<ConfigurationChangePreviewGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(activity);

        return new ConfigurationChangePreviewResponse
        {
            ActivityId = preview.ActivityId,
            Surface = preview.Surface,
            ActivityStatus = activity.Status,
            Message = activity.Message,
            ErrorMessage = activity.ErrorMessage,
            ObjectsProcessed = activity.ObjectsProcessed,
            ObjectsToProcess = activity.ObjectsToProcess,
            ValidationStatus = preview.ValidationStatus,
            ImpactCountsStatus = preview.ImpactCountsStatus,
            SummaryStatus = preview.SummaryStatus,
            DeltasStatus = preview.DeltasStatus,
            IsComplete = preview.IsComplete,
            HasFailed = preview.HasFailed,
            ValidationFindings = preview.ReadValidationFindings(),
            ImpactCounts = preview.ReadImpactCounts(),
            Groups = [.. groups.Select(ConfigurationChangePreviewGroupResponse.FromEntity)],
            EstimatedAffectedObjects = preview.EstimatedAffectedObjects,
            EstimatedDeltaRows = preview.EstimatedDeltaRows,
            DeltaPersistence = preview.DeltaPersistence,
            DispatchedToWorker = preview.DispatchedToWorker,
            StalenessBaseline = preview.StalenessBaseline
        };
    }
}
