// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Text.Json;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The speculative outcome tree node (#288 plan Phase 3, PRD decision D4): an unpersisted DTO carrying the
/// same story a real Run Profile Execution Item's outcome tree tells, mapped through one shared mapping so
/// preview and reality render identically and the fidelity paired test can diff them. These pin the mapping
/// and the serialisability requirement (PRD requirement 3: API-returnable, no EF navigation cycles).
/// </summary>
[TestFixture]
public class SyncOutcomeNodeTests
{
    [Test]
    public void FromSyncOutcome_MapsEveryDisplayFieldTheTreeRendersFrom()
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
            TargetEntityId = Guid.NewGuid(),
            TargetEntityDescription = "Corporate Directory",
            SyncRuleId = 7,
            SyncRuleName = "Users to Corporate Directory",
            DetailCount = 12,
            DetailMessage = "12 attributes flowed",
            Ordinal = 3
        };

        var node = SyncOutcomeNode.FromSyncOutcome(outcome);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.OutcomeType, Is.EqualTo(outcome.OutcomeType));
            Assert.That(node.TargetEntityId, Is.EqualTo(outcome.TargetEntityId));
            Assert.That(node.TargetEntityDescription, Is.EqualTo(outcome.TargetEntityDescription));
            Assert.That(node.SyncRuleId, Is.EqualTo(outcome.SyncRuleId));
            Assert.That(node.SyncRuleName, Is.EqualTo(outcome.SyncRuleName));
            Assert.That(node.DetailCount, Is.EqualTo(outcome.DetailCount));
            Assert.That(node.DetailMessage, Is.EqualTo(outcome.DetailMessage));
            Assert.That(node.Ordinal, Is.EqualTo(outcome.Ordinal));
        }
    }

    [Test]
    public void FromSyncOutcome_MapsChildrenRecursivelyInOrdinalOrder()
    {
        // Real trees are persisted flat and re-assembled with children in arbitrary insertion order; the
        // display sorts siblings by Ordinal, so the shared mapping must too or preview and reality would
        // render the same tree differently.
        var root = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            Ordinal = 0
        };
        var second = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            Ordinal = 2
        };
        var first = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            Ordinal = 1
        };
        var grandchild = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            Ordinal = 0
        };
        first.Children.Add(grandchild);
        root.Children.Add(second);
        root.Children.Add(first);

        var node = SyncOutcomeNode.FromSyncOutcome(root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Children, Has.Count.EqualTo(2));
            Assert.That(node.Children[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
            Assert.That(node.Children[1].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
            Assert.That(node.Children[0].Children, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void SyncOutcomeNode_SerialisesToJsonAndBack_WithoutEntityCycles()
    {
        // PRD requirement 3: the preview result must be API-returnable. The node deliberately carries no
        // parent pointer and no EF entity references, so default System.Text.Json settings must round-trip a
        // tree without a reference-cycle failure.
        var node = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
            TargetEntityDescription = "Corporate Directory",
            Children =
            [
                new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, DetailCount = 4 }
            ]
        };

        var json = JsonSerializer.Serialize(node);
        var roundTripped = JsonSerializer.Deserialize<SyncOutcomeNode>(json);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTripped, Is.Not.Null);
            Assert.That(roundTripped!.OutcomeType, Is.EqualTo(node.OutcomeType));
            Assert.That(roundTripped.Children, Has.Count.EqualTo(1));
            Assert.That(roundTripped.Children[0].DetailCount, Is.EqualTo(4));
        }
    }
}
