// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Completeness test for <see cref="OutcomeDisplayMap.GetSpeculativeLabel"/> (#1519, D-S1/D-S9): every
/// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType"/> that <c>SyncPreviewServer</c> can emit
/// must carry a conditional-mood label, so a speculative render never silently falls back to a
/// past-tense one. The set below was derived by grepping
/// <c>OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.</c> in
/// <c>src/JIM.Application/Servers/SyncPreviewServer.cs</c>; extend it if the preview engine grows a new
/// outcome type.
/// </summary>
[TestFixture]
public class OutcomeDisplayMapSpeculativeLabelTests
{
    private static readonly ActivityRunProfileExecutionItemSyncOutcomeType[] PreviewEmittableTypes =
    [
        ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
        ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
        ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
        ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
        ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
        ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled,
        ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor,
        ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin,
        ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
        ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
        ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
        ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved
    ];

    [TestCaseSource(nameof(PreviewEmittableTypes))]
    public void GetSpeculativeLabel_EveryPreviewEmittableOutcomeType_ReturnsNonNullLabel(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        var label = OutcomeDisplayMap.GetSpeculativeLabel(outcomeType);

        Assert.That(label, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void GetSpeculativeLabel_OutcomeTypeThePreviewEngineNeverEmits_ReturnsNull()
    {
        // Exported is never produced by SyncPreviewServer (it belongs to the export execution pipeline);
        // proves the accessor does not fabricate a label for a type outside the documented set.
        var label = OutcomeDisplayMap.GetSpeculativeLabel(ActivityRunProfileExecutionItemSyncOutcomeType.Exported);

        Assert.That(label, Is.Null);
    }
}
