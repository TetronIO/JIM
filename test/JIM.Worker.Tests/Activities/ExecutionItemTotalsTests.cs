// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// <see cref="ExecutionItemTotals"/> holds what an Activity's totals need from a set of execution items, so the items
/// themselves can be released (#2521): a confirming import writes the items reconciliation raises with their batch and
/// counts them only with the run's last items. Counted that way, the totals must be exactly those of counting every item
/// in one list, including the rule that one item with outcomes makes the whole list count by outcome.
/// </summary>
[TestFixture]
public class ExecutionItemTotalsTests
{
    [Test]
    public void AddTo_TotalsOfTwoListsMerged_EqualCountingBothListsAsOne()
    {
        // Raised by reconciliation: outcomes that count towards no total, and one error. Then the run's last items: one
        // with no outcome, which on its own would count as an update by its change type.
        var raised = new List<ActivityRunProfileExecutionItem>
        {
            Item(ObjectChangeType.Updated, ActivityRunProfileExecutionItemErrorType.NotSet, ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed),
            Item(ObjectChangeType.Updated, ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed, ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed)
        };
        var last = new List<ActivityRunProfileExecutionItem>
        {
            Item(ObjectChangeType.Updated, ActivityRunProfileExecutionItemErrorType.NotSet),
            Item(ObjectChangeType.Added, ActivityRunProfileExecutionItemErrorType.NotSet, ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded),
            Item(ObjectChangeType.Deleted, ActivityRunProfileExecutionItemErrorType.NotSet, ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected)
        };

        var asOneList = new Activity();
        Worker.AccumulateActivitySummaryStats(asOneList, [.. last, .. raised]);

        var merged = new Activity();
        var totals = ExecutionItemTotals.Of(raised);
        ExecutionItemTotals.Of(last).Add(totals).AddTo(merged);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.TotalAdded, Is.EqualTo(asOneList.TotalAdded).And.EqualTo(1));
            Assert.That(merged.TotalUpdated, Is.EqualTo(asOneList.TotalUpdated).And.EqualTo(0), "counted by outcome, as the one list is");
            Assert.That(merged.TotalDeleted, Is.EqualTo(asOneList.TotalDeleted).And.EqualTo(1));
            Assert.That(merged.TotalErrors, Is.EqualTo(asOneList.TotalErrors).And.EqualTo(1));
        }
    }

    [Test]
    public void AddTo_ItemsWithNoOutcomes_CountByChangeType()
    {
        var items = new List<ActivityRunProfileExecutionItem>
        {
            Item(ObjectChangeType.Updated, ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed),
            Item(ObjectChangeType.Updated, ActivityRunProfileExecutionItemErrorType.NotSet),
            Item(ObjectChangeType.Created, ActivityRunProfileExecutionItemErrorType.NotSet),
            Item(ObjectChangeType.OutOfScopeRetainJoin, null)
        };

        var expected = new Activity();
        Worker.AccumulateActivitySummaryStats(expected, items);
        var actual = new Activity { TotalUpdated = 3 };
        ExecutionItemTotals.Of(items).AddTo(actual);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.TotalUpdated, Is.EqualTo(3 + expected.TotalUpdated).And.EqualTo(5), "added to what the Activity holds");
            Assert.That(actual.TotalCreated, Is.EqualTo(expected.TotalCreated).And.EqualTo(1));
            Assert.That(actual.TotalOutOfScopeRetainJoin, Is.EqualTo(expected.TotalOutOfScopeRetainJoin).And.EqualTo(1));
            Assert.That(actual.TotalErrors, Is.EqualTo(expected.TotalErrors).And.EqualTo(1));
        }
    }

    [Test]
    public void IsEmpty_NoItemsAdded_IsTrue()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ExecutionItemTotals().IsEmpty, Is.True);
            Assert.That(ExecutionItemTotals.Of([Item(ObjectChangeType.Updated, null)]).IsEmpty, Is.False);
        }
    }

    private static ActivityRunProfileExecutionItem Item(ObjectChangeType changeType, ActivityRunProfileExecutionItemErrorType? errorType,
        params ActivityRunProfileExecutionItemSyncOutcomeType[] outcomes)
    {
        var item = new ActivityRunProfileExecutionItem { ObjectChangeType = changeType, ErrorType = errorType };
        foreach (var outcome in outcomes)
            item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = outcome });
        return item;
    }
}
