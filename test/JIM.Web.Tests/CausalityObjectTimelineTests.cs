// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Causality;
using JIM.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// An object's causal timeline in the Causality Explorer (#2100): what happened to the object, newest first, each row
/// with the run that did it and the nearest cause, and a way to explore any row's Lineage. It must say where its rows
/// are not the whole story, and never present a page of fifty as everything.
/// </summary>
[TestFixture]
public class CausalityObjectTimelineTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    private static readonly CausalityFocus Focus = new(CausalityFocusKind.ConnectedSystemObject, Guid.Parse("cccccccc-0000-0000-0000-000000000003"));

    [Test]
    public void Render_Row_NamesTheRunWhatHappenedAndTheNearestCauseAndExploresTheItem()
    {
        var row = Row(ObjectChangeType.Deprovisioned, "Deprovisioned:1", Cause("Tina Adams (S8-99)"));

        var cut = RenderTimeline([row], totalCount: 1);

        var rendered = cut.Find(".jim-causality-timeline-row");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rendered.TextContent, Does.Contain("Export").And.Contain("Glitterband EMEA"));
            Assert.That(rendered.TextContent, Does.Contain(Helpers.GetOutcomeTypeDisplayName(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned)));
            Assert.That(rendered.QuerySelector(".jim-causality-because")!.TextContent, Does.Contain("Tina Adams (S8-99)"));
            Assert.That(rendered.QuerySelector("a.jim-causality-explore")!.GetAttribute("href"),
                Is.EqualTo(Links().ItemHref(row.Entry.RunProfileExecutionItemId)));
        }
    }

    [Test]
    public void Render_RowWithMoreCausesThanShown_CountsTheRest()
    {
        var row = Row(ObjectChangeType.Exported, "Exported:1", Cause("Owen Hart"), totalCohorts: 3);

        var cut = RenderTimeline([row], totalCount: 1);

        Assert.That(cut.Find(".jim-causality-because").TextContent, Does.Contain("and 2 more causes"));
    }

    /// <summary>
    /// A row with no recorded cause says how its story ends, and a cut-short story never reads as a complete one.
    /// </summary>
    [TestCase(CausalChainResolution.NoFurtherCauses, "No earlier causes recorded")]
    [TestCase(CausalChainResolution.CauseNotRetained, "What caused this is no longer retained")]
    public void Render_RowWithNoCause_SaysHowItsStoryEnds(CausalChainResolution resolution, string expected)
    {
        var row = Row(ObjectChangeType.Updated, "CsoUpdated:1", cause: null, resolution: resolution);

        var cut = RenderTimeline([row], totalCount: 1);

        Assert.That(cut.Find(".jim-causality-because").TextContent.Trim(), Is.EqualTo(expected));
    }

    [Test]
    public async Task Render_FewerRowsThanTheObjectHas_OffersMoreAndSaysHowManyAreShownAsync()
    {
        var shownMore = 0;
        var rows = Enumerable.Range(0, 3).Select(_ => Row(ObjectChangeType.Updated, "CsoUpdated:1", Cause("x"))).ToList();

        var cut = RenderTimeline(rows, totalCount: 120, onShowMore: () => shownMore++);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-causality-timeline-count").TextContent, Does.Contain("3 of 120"));
        }

        await cut.Find("button.jim-causality-show-more").ClickAsync(new MouseEventArgs());
        Assert.That(shownMore, Is.EqualTo(1));
    }

    [Test]
    public void Render_EveryRowShown_OffersNoMore()
    {
        var cut = RenderTimeline([Row(ObjectChangeType.Updated, "CsoUpdated:1", Cause("x"))], totalCount: 1);

        Assert.That(cut.FindAll("button.jim-causality-show-more"), Is.Empty);
    }

    /// <summary>
    /// Without Metaverse Object change tracking, the rows are only what the object's joined records recorded, and the
    /// timeline says so rather than implying it is the whole story (PRD Scenario 5).
    /// </summary>
    [Test]
    public void Render_MetaverseObjectChangesNotRecorded_SaysTheRowsAreIncomplete()
    {
        var cut = RenderTimeline([Row(ObjectChangeType.AttributeFlow, "AttributeFlow:1", Cause("x"))], totalCount: 1,
            changesNotRecorded: true);

        Assert.That(cut.Find(".jim-causality-timeline-note").TextContent, Does.Contain("change tracking is off"));
    }

    [Test]
    public void Render_DeletedConnectedSystemObject_SaysHowItsItemsWereFound()
    {
        var header = new CausalObjectHeader
        {
            Subject = Focus.Subject!, IsDeleted = true, ConnectedSystemName = "Glitterband EMEA"
        };

        var cut = RenderTimeline([Row(ObjectChangeType.Deprovisioned, "Deprovisioned:1", Cause("x"))], totalCount: 1, header: header);

        Assert.That(cut.Find(".jim-causality-timeline-note").TextContent, Does.Contain("external ID within Glitterband EMEA"));
    }

    [Test]
    public void Render_NothingRecorded_SaysSo()
    {
        var cut = RenderTimeline([], totalCount: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".jim-causality-timeline-row"), Is.Empty);
            Assert.That(cut.Markup, Does.Contain("Nothing recorded for this object"));
        }
    }

    /// <summary>
    /// A follow-up of a row's item (#2668), such as the Initial Password of the account an export provisioned, is one row
    /// directly beneath the item's row, showing where it stands now; a row with none has nothing beneath it.
    /// </summary>
    [Test]
    public void Render_RowWithAFollowUp_ShowsItBeneathThatRowOnly()
    {
        var export = Row(ObjectChangeType.Exported, "Exported:1", Cause("Owen Hart"));
        var other = Row(ObjectChangeType.Updated, "CsoUpdated:1", Cause("Owen Hart"));
        var followUp = new JIM.Web.Models.ExecutionItemFollowUpDisplay(Guid.NewGuid(), "Initial password", "Glitterband EMEA",
            "Retrying", "warn", "Timed out. Next attempt soon", true, DateTime.UtcNow);
        var followUps = new Dictionary<Guid, IReadOnlyList<JIM.Web.Models.ExecutionItemFollowUpDisplay>>
        {
            [export.Entry.RunProfileExecutionItemId] = [followUp]
        };

        var cut = RenderTimeline([other, export], totalCount: 2, followUps: followUps);

        var rows = cut.FindAll("tbody tr");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(3), "the two items, and the one follow-up");
            Assert.That(rows[2].ClassList, Does.Contain("jim-causality-timeline-followup"), "beneath the export, its row's own item");
            Assert.That(rows[2].TextContent, Does.Contain("Initial password").And.Contain("Retrying"));
            Assert.That(rows[2].QuerySelector("a")!.GetAttribute("href"), Is.EqualTo(followUp.ActivityHref));
        }
    }

    private IRenderedComponent<CausalityObjectTimeline> RenderTimeline(List<CausalTimelineRow> rows, int totalCount,
        Action? onShowMore = null, bool changesNotRecorded = false, CausalObjectHeader? header = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<JIM.Web.Models.ExecutionItemFollowUpDisplay>>? followUps = null)
    {
        return Render<CausalityObjectTimeline>(ps =>
        {
            ps.Add(c => c.Rows, rows);
            ps.Add(c => c.TotalCount, totalCount);
            ps.Add(c => c.Links, Links());
            ps.Add(c => c.MetaverseObjectChangesNotRecorded, changesNotRecorded);
            ps.Add(c => c.Header, header);
            if (followUps != null)
                ps.Add(c => c.FollowUps, followUps);
            if (onShowMore != null)
                ps.Add(c => c.OnShowMore, onShowMore);
        });
    }

    private static CausalityExplorerLinks Links() => new(CausalityTrail.Empty, Focus);

    private static CausalChainCohort Cause(string deletedName) => new()
    {
        EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedDeprovision,
        ReasonCode = CausalReasonCode.ExportDeleteStaged,
        ConnectedSystemId = 1,
        ConnectedSystemName = "Yellowstone APAC",
        Members = [new CausalChainMember { DisplayName = deletedName, RunProfileExecutionItemId = Guid.NewGuid(), Occurred = DateTime.UtcNow }]
    };

    private static CausalTimelineRow Row(ObjectChangeType changeType, string outcomeSummary, CausalChainCohort? cause,
        int totalCohorts = 1, CausalChainResolution resolution = CausalChainResolution.Resolved)
    {
        var itemId = Guid.NewGuid();
        return new CausalTimelineRow
        {
            Entry = new CausalTimelineEntry
            {
                RunProfileExecutionItemId = itemId,
                Executed = DateTime.UtcNow.AddHours(-2),
                ActivityTargetType = ActivityTargetType.ConnectedSystemRunProfile,
                ActivityTargetName = "Export",
                ActivityTargetContext = "Glitterband EMEA",
                ObjectChangeType = changeType,
                OutcomeSummary = outcomeSummary,
                DisplayName = "tina.adams99",
                ObjectType = "person"
            },
            NearestCauses = new CausalNearestCauses
            {
                RunProfileExecutionItemId = itemId,
                Cohorts = cause == null ? [] : [cause],
                TotalCohortCount = cause == null ? 0 : totalCohorts,
                Resolution = cause == null ? resolution : CausalChainResolution.Resolved
            }
        };
    }
}
