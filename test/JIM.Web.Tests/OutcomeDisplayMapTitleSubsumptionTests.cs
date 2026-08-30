// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="OutcomeDisplayMap.IsTitleSubsumedByOperation"/> (#1495 second follow-up): the
/// single source of truth for which outcomes' Lineage card heads are redundant once the join label
/// between columns and the operation chip both already state the outcome.
/// </summary>
[TestFixture]
public class OutcomeDisplayMapTitleSubsumptionTests
{
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Projected)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Joined)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned)]
    public void IsTitleSubsumedByOperation_TheThreeJoinLabelledOutcomes_AreTrue(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        Assert.That(OutcomeDisplayMap.IsTitleSubsumedByOperation(outcomeType), Is.True);
    }

    /// <summary>
    /// Exported is deliberately out of scope: its decision-specific titles ("Record created", "Changes
    /// applied", "Record deleted") are not restated anywhere else on the panel, unlike the three join
    /// labels above, so its card head must keep rendering.
    /// </summary>
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Exported)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope)]
    public void IsTitleSubsumedByOperation_EveryOtherOutcome_IsFalse(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        Assert.That(OutcomeDisplayMap.IsTitleSubsumedByOperation(outcomeType), Is.False);
    }
}
