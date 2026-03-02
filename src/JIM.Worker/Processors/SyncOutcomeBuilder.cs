using JIM.Models.Activities;

namespace JIM.Worker.Processors;

/// <summary>
/// Static helper for building sync outcome tree nodes on RPEIs.
/// Centralises outcome node creation and summary generation so that
/// integration points in the sync processor remain clean and consistent.
/// </summary>
internal static class SyncOutcomeBuilder
{
    /// <summary>
    /// Creates a root outcome node and adds it to the RPEI's SyncOutcomes collection.
    /// Returns the node so children can be attached in Detailed mode.
    /// </summary>
    internal static ActivityRunProfileExecutionItemSyncOutcome AddRootOutcome(
        ActivityRunProfileExecutionItem rpei,
        ActivityRunProfileExecutionItemSyncOutcomeType type,
        Guid? targetEntityId = null,
        string? targetEntityDescription = null,
        int? detailCount = null,
        string? detailMessage = null)
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = type,
            TargetEntityId = targetEntityId,
            TargetEntityDescription = targetEntityDescription,
            DetailCount = detailCount,
            DetailMessage = detailMessage,
            Ordinal = rpei.SyncOutcomes.Count
        };

        rpei.SyncOutcomes.Add(outcome);
        return outcome;
    }

    /// <summary>
    /// Creates a child outcome node under a parent and adds it to the RPEI's SyncOutcomes collection.
    /// The parent-child FK relationship is resolved during bulk insert flattening.
    /// </summary>
    internal static ActivityRunProfileExecutionItemSyncOutcome AddChildOutcome(
        ActivityRunProfileExecutionItem rpei,
        ActivityRunProfileExecutionItemSyncOutcome parent,
        ActivityRunProfileExecutionItemSyncOutcomeType type,
        Guid? targetEntityId = null,
        string? targetEntityDescription = null,
        int? detailCount = null,
        string? detailMessage = null)
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            ParentSyncOutcome = parent,
            OutcomeType = type,
            TargetEntityId = targetEntityId,
            TargetEntityDescription = targetEntityDescription,
            DetailCount = detailCount,
            DetailMessage = detailMessage,
            Ordinal = parent.Children.Count
        };

        parent.Children.Add(outcome);
        rpei.SyncOutcomes.Add(outcome);
        return outcome;
    }

    /// <summary>
    /// Builds the denormalised OutcomeSummary string from the RPEI's SyncOutcomes collection.
    /// Format: "Projected:1,AttributeFlow:12,PendingExportCreated:2" — counts per outcome type.
    /// Only counts root-level outcomes (no children) to keep the summary concise.
    /// </summary>
    internal static void BuildOutcomeSummary(ActivityRunProfileExecutionItem rpei)
    {
        if (rpei.SyncOutcomes.Count == 0)
            return;

        // Count root-level outcomes (those without a parent) by type
        var rootOutcomeCounts = rpei.SyncOutcomes
            .Where(o => o.ParentSyncOutcome == null && o.ParentSyncOutcomeId == null)
            .GroupBy(o => o.OutcomeType)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Count()}")
            .ToList();

        if (rootOutcomeCounts.Count > 0)
            rpei.OutcomeSummary = string.Join(",", rootOutcomeCounts);
    }
}
