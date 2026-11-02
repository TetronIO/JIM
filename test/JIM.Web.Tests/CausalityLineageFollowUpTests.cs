// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Web.Causality;
using JIM.Web.Models;
using NUnit.Framework;
using static JIM.Web.Tests.CausalityConsequenceTestData;

namespace JIM.Web.Tests;

/// <summary>
/// An execution item's follow-ups in the Lineage (#2668): work the item set in motion that finishes after its run, such as
/// the Initial Password of the account an export provisioned. Each is a card under After this run on the item's own record,
/// because the work happens to that account, not to another object; and the footer says while any is still running, so
/// the Lineage never claims a story has ended while JIM is still working on it.
/// </summary>
[TestFixture]
public class CausalityLineageFollowUpTests
{
    [Test]
    public void Build_ItemWithAFollowUp_PlacesItAfterThisRunOnTheItemsOwnRecord()
    {
        var followUp = FollowUp("Delivered", running: false);

        var lineage = Build([followUp], consequences: null);

        var record = lineage.Columns.SelectMany(c => c.Objects).Single(o => o.Title == "S8-99");
        var card = record.Consequences.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(card.Kind, Is.EqualTo(CausalityLineageConsequenceKind.FollowUp));
            Assert.That(card.FollowUp, Is.SameAs(followUp));
            Assert.That(card.DisplayTitle, Is.EqualTo("Initial password"));
            Assert.That(card.LinkHref, Is.EqualTo(followUp.ActivityHref));
        }
    }

    [Test]
    public void Build_FollowUpStillRunning_FooterSaysTheStoryHasNotEnded()
    {
        var lineage = Build([FollowUp("Retrying", running: true)], Consequences([]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lineage.Consequences, Is.Not.Null);
            Assert.That(lineage.Consequences!.StateSentences, Is.EqualTo(new[] { "1 follow-up is still running: Initial password, retrying." }));
            Assert.That(lineage.Consequences.HasWaitingBranches, Is.True);
        }
    }

    [Test]
    public void Build_FollowUpFinished_FooterSaysEveryBranchHasRunToItsEnd()
    {
        var lineage = Build([FollowUp("Delivered", running: false)], consequences: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lineage.Consequences, Is.Not.Null, "a follow-up is something that followed from this run");
            Assert.That(lineage.Consequences!.StateSentences, Is.EqualTo(new[] { "Every branch has run to its end." }));
            Assert.That(lineage.Consequences.HasWaitingBranches, Is.False);
        }
    }

    [Test]
    public void Build_NoFollowUps_LeavesTheLineageAsItWas()
    {
        var lineage = Build([], consequences: null);

        Assert.That(lineage.Consequences, Is.Null);
    }

    private static CausalityLineageModel Build(IReadOnlyList<ExecutionItemFollowUpDisplay> followUps, JIM.Models.Activities.DTOs.CausalConsequences? consequences)
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ObjectChangeType = ObjectChangeType.Exported };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported, parent: null, ordinal: 0, detailCount: 1);
        var model = CausalityModelBuilder.Build(item, ImportContext());
        return CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Exported, consequences, followUps);
    }

    private static ExecutionItemFollowUpDisplay FollowUp(string state, bool running) =>
        new(Guid.NewGuid(), "Initial password", "Yellowstone APAC", state, running ? "warn" : "ok", "Next attempt soon", running, T0);
}
