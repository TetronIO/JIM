// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Staging.DTOs;

namespace JIM.Application.Services;

/// <summary>
/// Decides when a configuration change's predicted counts may be shown beside what the synchronisation that applied it
/// did (#2618).
/// <para>
/// The value is the withholding. A preview and a run share one outcome vocabulary, but most values do not mean the same
/// thing in both: a matching preview's "would join instead of project" is a difference from today that no run records,
/// and a scoping preview's "would fall into scope" has no run equivalent. Two numbers side by side read as a test of the
/// preview whatever the caption says, so a comparison is made only for an outcome that is known to be counted the same
/// way by both: the scoping preview's Projected and Joined are the objects the proposed scope newly lets in, which is
/// what a run records when it projects or joins them.
/// </para>
/// </summary>
public static class ConfigurationChangePredictionPolicy
{
    /// <summary>
    /// The outcomes a preview of each surface counts in the same way a run records them. Add an outcome here only after
    /// checking that the preview's count and the run's count measure the same objects.
    /// </summary>
    private static readonly IReadOnlyDictionary<ConfigurationChangePreviewSurface, IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcomeType>> ComparableBySurface =
        new Dictionary<ConfigurationChangePreviewSurface, IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcomeType>>
        {
            [ConfigurationChangePreviewSurface.SynchronisationRuleScope] =
            [
                ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                ActivityRunProfileExecutionItemSyncOutcomeType.Joined
            ]
        };

    /// <summary>
    /// The outcomes whose predicted and actual counts may be shown for a saved change, or none when they may not. Does no
    /// I/O: the caller reads the actual counts only for a non-empty answer.
    /// </summary>
    /// <param name="saveActivity">The configuration change Activity that recorded the save.</param>
    /// <param name="preview">The preview that informed it, or null where it is gone.</param>
    /// <param name="previewActivity">The preview's Activity, or null where it is gone.</param>
    /// <param name="change">The one change the run applied for the Object Type, applied.</param>
    /// <param name="changesBehindRun">How many changes the applying run claimed for the Object Type. Several mean each
    /// preview was made against a different version of the rules, and the run has one set of actual counts.</param>
    public static IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcomeType> ComparableOutcomes(
        Activity saveActivity,
        ConfigurationChangePreview? preview,
        Activity? previewActivity,
        ConfigurationChangeSynchronisationSummary change,
        int changesBehindRun)
    {
        ArgumentNullException.ThrowIfNull(saveActivity);
        ArgumentNullException.ThrowIfNull(change);

        // A preview informs a save only where the save cites it, and one something overtook describes a different
        // configuration from the one saved.
        if (saveActivity.PreviewActivityId == null || saveActivity.PreviewOvertakenAt != null)
            return [];

        if (changesBehindRun != 1 || change.AppliedAt == null || change.ClaimedByActivityId == null)
            return [];

        // The same rule as a save confirmation: a preview that failed or is still counting says nothing.
        if (preview is not { HasFailed: false, ImpactCountsStatus: ConfigurationChangePreviewStageStatus.Complete })
            return [];

        if (!ComparableBySurface.TryGetValue(preview.Surface, out var outcomes))
            return [];

        // The preview must be of the rule that was saved; its counts say nothing about another rule's change.
        if (change.SyncRuleId == null || previewActivity?.SyncRuleId != change.SyncRuleId)
            return [];

        return outcomes;
    }

    /// <summary>
    /// Pairs the preview's counts for a Connected System with the run's, for the outcomes <see cref="ComparableOutcomes"/>
    /// allowed. Null when neither side counted any of them, so there is no empty column to show.
    /// </summary>
    /// <param name="previewActivityId">The preview.</param>
    /// <param name="preview">The preview, already allowed by <see cref="ComparableOutcomes"/>.</param>
    /// <param name="connectedSystemId">The Connected System whose synchronisation applied the change.</param>
    /// <param name="outcomes">What <see cref="ComparableOutcomes"/> returned.</param>
    /// <param name="actual">The run's count of each outcome, limited to the processed Object Type's execution items.</param>
    public static ConfigurationChangePredictionComparison? Compare(
        Guid previewActivityId,
        ConfigurationChangePreview preview,
        int connectedSystemId,
        IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcomeType> outcomes,
        IReadOnlyDictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int> actual)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(actual);

        var predicted = preview.ReadImpactCounts()
            .Where(count => count.ConnectedSystemId == connectedSystemId && outcomes.Contains(count.TransitionType))
            .GroupBy(count => count.TransitionType)
            .ToDictionary(group => group.Key, group => group.Sum(count => count.ObjectCount));

        var lines = outcomes
            .Select(outcome => new ConfigurationChangePredictionLine(outcome, predicted.GetValueOrDefault(outcome), actual.GetValueOrDefault(outcome)))
            .Where(line => line.Predicted > 0 || line.Actual > 0)
            .OrderByDescending(line => line.Predicted)
            .ThenBy(line => line.Outcome)
            .ToList();

        return lines.Count == 0
            ? null
            : new ConfigurationChangePredictionComparison { PreviewActivityId = previewActivityId, Lines = lines };
    }
}
