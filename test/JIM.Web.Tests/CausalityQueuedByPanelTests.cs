// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A Pending Export's Queued by panel (#2100): the runs that staged it, newest first, each with what it did and why,
/// and a way into the Causality Explorer on that run's item. Where it cannot name every run it says so, and where it
/// names none it says why rather than looking empty.
/// </summary>
[TestFixture]
public class CausalityQueuedByPanelTests : JimComponentTestContext
{
    [Test]
    public void Render_StagingRuns_ListsEachWithItsCauseAndExploresItsItem()
    {
        var newest = Row("Full Synchronisation", "Owen Hart");
        var older = Row("Delta Synchronisation", "Tina Adams");

        var cut = RenderPanel(new CausalQueuedBy { PendingExportId = Guid.NewGuid(), Rows = [newest, older] });

        var rows = cut.FindAll(".jim-causality-queued-row");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].TextContent, Does.Contain("Full Synchronisation").And.Contain("Owen Hart"));
            Assert.That(rows[1].TextContent, Does.Contain("Delta Synchronisation").And.Contain("Tina Adams"));
            Assert.That(rows[0].QuerySelector("a.jim-causality-explore")!.GetAttribute("href"),
                Is.EqualTo($"/causality/items/{newest.Entry.RunProfileExecutionItemId}"));
        }
    }

    /// <summary>
    /// An export staged before runs were recorded on it, or by a path that records no item, names none: the panel says
    /// so, and that its changes are still exactly what will be exported.
    /// </summary>
    [Test]
    public void Render_NoRecordedRun_SaysWhyRatherThanLookingEmpty()
    {
        var cut = RenderPanel(new CausalQueuedBy { PendingExportId = Guid.NewGuid() });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".jim-causality-queued-row"), Is.Empty);
            Assert.That(cut.Find(".jim-causality-queued-empty").TextContent, Does.Contain("No run is recorded as staging this Pending Export"));
        }
    }

    [Test]
    public void Render_StagingRunsNoLongerRetained_AreCountedRatherThanDropped()
    {
        var cut = RenderPanel(new CausalQueuedBy { PendingExportId = Guid.NewGuid(), Rows = [Row("Export", "x")], NotRetainedCount = 2 });

        Assert.That(cut.Find(".jim-causality-queued-note").TextContent, Does.Contain("2 earlier runs").And.Contain("no longer retained"));
    }

    [Test]
    public void Render_EveryStagerRemoved_SaysTheRunsAreNoLongerRetained()
    {
        var cut = RenderPanel(new CausalQueuedBy { PendingExportId = Guid.NewGuid(), NotRetainedCount = 1 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".jim-causality-queued-empty"), Is.Empty, "runs were recorded; they are what was removed");
            Assert.That(cut.Find(".jim-causality-queued-note").TextContent, Does.Contain("1 run").And.Contain("no longer retained"));
        }
    }

    [Test]
    public void Render_StagerListFull_SaysEarlierRunsMayBeMissing()
    {
        var cut = RenderPanel(new CausalQueuedBy { PendingExportId = Guid.NewGuid(), Rows = [Row("Export", "x")], EarlierRunsMayBeOmitted = true });

        Assert.That(cut.Find(".jim-causality-queued-note").TextContent, Does.Contain("most recent"));
    }

    private IRenderedComponent<CausalityQueuedByPanel> RenderPanel(CausalQueuedBy queuedBy) =>
        Render<CausalityQueuedByPanel>(ps => ps.Add(c => c.QueuedBy, queuedBy));

    private static CausalTimelineRow Row(string run, string causeName)
    {
        var itemId = Guid.NewGuid();
        return new CausalTimelineRow
        {
            Entry = new CausalTimelineEntry
            {
                RunProfileExecutionItemId = itemId,
                Executed = DateTime.UtcNow.AddMinutes(-20),
                ActivityTargetType = ActivityTargetType.ConnectedSystemRunProfile,
                ActivityTargetName = run,
                ActivityTargetContext = "Yellowstone APAC",
                ObjectChangeType = ObjectChangeType.AttributeFlow,
                OutcomeSummary = "PendingExportCreated:1",
                DisplayName = "Project-Pulse"
            },
            NearestCauses = new CausalNearestCauses
            {
                RunProfileExecutionItemId = itemId,
                TotalCohortCount = 1,
                Resolution = CausalChainResolution.Resolved,
                Cohorts =
                [
                    new CausalChainCohort
                    {
                        EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                        ConnectedSystemId = 1,
                        ConnectedSystemName = "Yellowstone APAC",
                        AttributeName = "Static Members",
                        Members = [new CausalChainMember { DisplayName = causeName, RunProfileExecutionItemId = Guid.NewGuid(), Occurred = DateTime.UtcNow }]
                    }
                ]
            }
        };
    }
}
