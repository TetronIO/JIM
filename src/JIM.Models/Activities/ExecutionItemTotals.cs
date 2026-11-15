// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;

namespace JIM.Models.Activities;

/// <summary>
/// What an Activity's summary totals need from a set of Run Profile Execution Items, held as counts so the items can be
/// released once written. Merged with the totals of other items and added to the Activity, it gives exactly the totals of
/// counting every item in one list: by outcome when any of them has one, otherwise by change type (#2521).
/// </summary>
public sealed class ExecutionItemTotals
{
    private readonly Dictionary<ObjectChangeType, int> _itemsByChangeType = [];
    private readonly Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int> _outcomesByType = [];
    private int _itemCount;
    private int _itemsWithErrors;
    private bool _anyItemHasOutcomes;

    /// <summary>Whether no item has been counted.</summary>
    public bool IsEmpty => _itemCount == 0;

    /// <summary>The totals of the given items.</summary>
    public static ExecutionItemTotals Of(IEnumerable<ActivityRunProfileExecutionItem> items) => new ExecutionItemTotals().Add(items);

    /// <summary>Counts the given items in. Returns these totals.</summary>
    public ExecutionItemTotals Add(IEnumerable<ActivityRunProfileExecutionItem> items)
    {
        foreach (var item in items)
        {
            _itemCount++;
            _itemsByChangeType[item.ObjectChangeType] = _itemsByChangeType.GetValueOrDefault(item.ObjectChangeType) + 1;
            if (item.ErrorType.HasValue && item.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
                _itemsWithErrors++;

            // Every outcome, root and child alike: an item's outcomes are one flat list.
            _anyItemHasOutcomes |= item.SyncOutcomes.Count > 0;
            foreach (var outcomeType in item.SyncOutcomes.Select(outcome => outcome.OutcomeType))
                _outcomesByType[outcomeType] = _outcomesByType.GetValueOrDefault(outcomeType) + 1;
        }

        return this;
    }

    /// <summary>Counts other totals in, as though their items were among these. Returns these totals.</summary>
    public ExecutionItemTotals Add(ExecutionItemTotals other)
    {
        _itemCount += other._itemCount;
        _itemsWithErrors += other._itemsWithErrors;
        _anyItemHasOutcomes |= other._anyItemHasOutcomes;
        foreach (var (changeType, count) in other._itemsByChangeType)
            _itemsByChangeType[changeType] = _itemsByChangeType.GetValueOrDefault(changeType) + count;
        foreach (var (outcomeType, count) in other._outcomesByType)
            _outcomesByType[outcomeType] = _outcomesByType.GetValueOrDefault(outcomeType) + count;
        return this;
    }

    /// <summary>
    /// Adds these totals to the Activity's running totals. Derived from the items' outcomes when any item has one, and from
    /// their change types otherwise; items created, kept joined out of scope, and in error are always counted per item.
    /// </summary>
    public void AddTo(Activity activity)
    {
        if (_anyItemHasOutcomes)
        {
            activity.TotalAdded += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded);
            activity.TotalUpdated += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);
            activity.TotalDeleted += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted)
                + Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected)
                + Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);

            activity.TotalProjected += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
            activity.TotalJoined += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
            activity.TotalAttributeFlows += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
            activity.TotalDisconnected += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected);
            activity.TotalDisconnectedOutOfScope += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);

            activity.TotalExported += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.Exported);
            activity.TotalDeprovisioned += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned);

            activity.TotalPendingExports += _outcomesByType.Where(pair => SyncOutcomeTypes.IsPendingExport(pair.Key)).Sum(pair => pair.Value);
            activity.TotalDriftCorrections += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection);
            activity.TotalProvisioned += Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);
        }
        else
        {
            activity.TotalAdded += Items(ObjectChangeType.Added);
            activity.TotalUpdated += Items(ObjectChangeType.Updated);
            activity.TotalDeleted += Items(ObjectChangeType.Deleted);

            activity.TotalProjected += Items(ObjectChangeType.Projected);
            activity.TotalJoined += Items(ObjectChangeType.Joined);
            activity.TotalAttributeFlows += Items(ObjectChangeType.AttributeFlow);

            activity.TotalDisconnected += Items(ObjectChangeType.Disconnected);
            activity.TotalDisconnectedOutOfScope += Items(ObjectChangeType.DisconnectedOutOfScope);

            activity.TotalExported += Items(ObjectChangeType.Exported);
            activity.TotalDeprovisioned += Items(ObjectChangeType.Deprovisioned);

            activity.TotalPendingExports += Items(ObjectChangeType.PendingExport);
            activity.TotalDriftCorrections += Items(ObjectChangeType.DriftCorrection);
        }

        activity.TotalOutOfScopeRetainJoin += Items(ObjectChangeType.OutOfScopeRetainJoin);
        activity.TotalCreated += Items(ObjectChangeType.Created);
        activity.TotalErrors += _itemsWithErrors;
    }

    private int Outcomes(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType) => _outcomesByType.GetValueOrDefault(outcomeType);

    private int Items(ObjectChangeType changeType) => _itemsByChangeType.GetValueOrDefault(changeType);
}
