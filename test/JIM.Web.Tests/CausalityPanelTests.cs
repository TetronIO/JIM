// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityPanel"/>: rendering across the PRD scenarios, the view
/// switcher (Flow default; Timeline and Graph selectable; stored preferences honoured with graceful
/// fallback for unknown values), the technical-names toggle persisting via a stubbed
/// <see cref="JIM.Web.Services.IUserPreferenceService"/>, the shared attribute drawer, and the
/// empty (not-tracked) state.
/// </summary>
[TestFixture]
public class CausalityPanelTests
{
    /// <summary>
    /// How many attribute rows a rendered detail table is actually showing. The rows live in a virtualised grid,
    /// which brackets them with two empty spacer rows (that is how a virtualiser reserves the height of what it
    /// has not rendered), so counting every row in the body counts two that carry nothing.
    /// </summary>
    private static int AttributeRowCount(IReadOnlyList<IElement> rows) =>
        rows.Count(row => row.Children.Length > 0);

    private BunitContext _context = null!;
    private FakeUserPreferenceService _preferences = null!;

    [SetUp]
    public void SetUp()
    {
        _context = CausalityBunitContext.Create();
        _preferences = new FakeUserPreferenceService();
        _context.Services.AddSingleton<JIM.Web.Services.IUserPreferenceService>(_preferences);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _context.DisposeAsync();
    }

    private IRenderedComponent<CausalityPanel> RenderPanel(
        ActivityRunProfileExecutionItem item, CausalityPageContext context, DateTime? timestamp = null)
    {
        return _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.Item, item)
            .Add(c => c.Context, context)
            .Add(c => c.Timestamp, timestamp));
    }

    [Test]
    public void Render_NewJoinerScenario_RendersSummaryBandAndFlowViewByDefault()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".summary-sentence"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".flow-cols"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".tl"), Is.Empty);
        Assert.That(cut.FindAll(".oc-pill"), Is.Not.Empty);
    }

    [Test]
    public void Render_LeaverScenario_RendersWithoutException()
    {
        var cut = RenderPanel(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".evt-card").Count, Is.GreaterThan(1));
        Assert.That(cut.FindAll("a[href='/admin/deleted-objects?t=deleted-mvos&mvo=11111111-1111-1111-1111-111111111111']"), Is.Not.Empty);
    }

    [Test]
    public void Render_ExportFailureScenario_RendersWithoutException()
    {
        var cut = RenderPanel(CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext());

        Assert.That(cut.FindAll(".evt-card").Count, Is.GreaterThan(1));
        var badges = cut.FindAll(".evt-badge").Select(b => b.TextContent.Trim());
        Assert.That(badges, Does.Contain("Needs attention"));
    }

    [Test]
    public void Render_NoOutcomes_ShowsTheNotTrackedAlert()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };

        var cut = RenderPanel(item, CausalityTestData.NewJoinerContext());

        Assert.That(cut.Markup, Does.Contain("Outcome tracking was not enabled"));
        Assert.That(cut.FindAll(".flow-cols"), Is.Empty);
    }

    [Test]
    public void Render_ViewSwitcher_ShowsAllFourViewsWithFlowOn()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var buttons = cut.FindAll(".seg button");
        Assert.That(buttons.Select(b => b.TextContent.Trim()), Is.EqualTo(new[] { "Flow", "Timeline", "Graph", "Spine" }));
        Assert.That(cut.FindAll(".seg button")[0].ClassList, Does.Contain("on"));
        Assert.That(cut.FindAll(".seg button")[1].ClassList, Does.Not.Contain("on"));
        Assert.That(cut.FindAll(".seg button")[2].ClassList, Does.Not.Contain("on"));
        Assert.That(cut.FindAll(".seg button")[3].ClassList, Does.Not.Contain("on"));
    }

    [Test]
    public void ViewSwitcher_SelectingSpine_SwitchesTheViewAndPersistsThePreference()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        cut.FindAll(".seg button")[3].Click();

        Assert.That(cut.FindAll(".sp-canvas"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".flow-cols"), Is.Empty);
        Assert.That(_preferences.CausalityViewWrites, Is.EqualTo(new[] { "spine" }));
    }

    [Test]
    public void Render_PersistedSpinePreference_StartsOnTheSpine()
    {
        _preferences.StoredCausalityView = "spine";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".sp-canvas"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".flow-cols"), Is.Empty);
        Assert.That(cut.FindAll(".seg button")[3].ClassList, Does.Contain("on"));
        Assert.That(_preferences.CausalityViewWrites, Is.Empty);
    }

    [Test]
    public void SpineView_Active_ReplacesTheCausedBySection()
    {
        _preferences.StoredCausalityView = "spine";
        var item = CausalityTestData.NewJoinerItem();
        var chain = CausalityTestData.Chain(item.Id, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.ExportCausedImportConfirmation,
                connectedSystemId: 1, connectedSystemName: "Yellowstone APAC",
                members: CausalityTestData.Member("Liam Allen", Guid.NewGuid(),
                    CausalChainResolution.NoFurtherCauses)));

        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.Item, item)
            .Add(c => c.Context, CausalityTestData.NewJoinerContext())
            .Add(c => c.Chain, chain));

        // The chain renders on the canvas itself, so the separate list would say it all twice.
        Assert.That(cut.FindAll(".caused-by"), Is.Empty);
        Assert.That(cut.FindAll(".sp-card"), Is.Not.Empty);

        cut.FindAll(".seg button")[0].Click();

        Assert.That(cut.FindAll(".caused-by"), Has.Count.EqualTo(1),
            "the older views keep the Caused by list until they retire");
    }

    [Test]
    public void ViewSwitcher_SelectingTimeline_SwitchesTheViewAndPersistsThePreference()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        cut.FindAll(".seg button")[1].Click();

        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".flow-cols"), Is.Empty);
        Assert.That(_preferences.CausalityViewWrites, Is.EqualTo(new[] { "timeline" }));
    }

    [Test]
    public void Render_PersistedTimelinePreference_StartsOnTheTimeline()
    {
        _preferences.StoredCausalityView = "timeline";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".flow-cols"), Is.Empty);
        Assert.That(cut.FindAll(".seg button")[1].ClassList, Does.Contain("on"));
    }

    [Test]
    public void Render_PersistedFlowPreference_StartsOnTheFlowView()
    {
        _preferences.StoredCausalityView = "flow";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".flow-cols"), Has.Count.EqualTo(1));
        Assert.That(_preferences.CausalityViewWrites, Is.Empty);
    }

    [Test]
    public void Render_PersistedGraphPreference_StartsOnTheGraphView()
    {
        // Phase 2/3 stored "graph" preferences were held without taking effect; now the Graph view
        // exists, the stored preference must resolve to it
        _preferences.StoredCausalityView = "graph";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".graph-svg"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".flow-cols"), Is.Empty);
        Assert.That(cut.FindAll(".seg button")[2].ClassList, Does.Contain("on"));
        Assert.That(_preferences.CausalityViewWrites, Is.Empty);
    }

    [Test]
    public void Render_PersistedUnknownViewPreference_FallsBackToFlowWithoutOverwritingIt()
    {
        _preferences.StoredCausalityView = "constellation";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        // An unknown stored value renders the default Flow view without clobbering the stored
        // preference, so it takes effect if that view ever ships
        Assert.That(cut.FindAll(".flow-cols"), Has.Count.EqualTo(1));
        Assert.That(_preferences.CausalityViewWrites, Is.Empty);
        Assert.That(_preferences.StoredCausalityView, Is.EqualTo("constellation"));
    }

    [Test]
    public void Render_PersistedTechNamesPreference_StartsWithTechnicalEmphasis()
    {
        _preferences.StoredCausalityTechNames = true;

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.Find(".toggle-line").ClassList, Does.Contain("on"));
        var titles = cut.FindAll(".evt-title").Select(t => t.TextContent.Trim()).ToList();
        Assert.That(titles.Any(t => t.StartsWith("MVO Projected")), Is.True);
    }

    [Test]
    public void TechToggle_Click_PersistsViaThePreferenceServiceAndSwapsEmphasis()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        cut.Find(".toggle-line").Click();

        Assert.That(_preferences.CausalityTechNamesWrites, Is.EqualTo(new[] { true }));
        Assert.That(cut.Find(".toggle-line").ClassList, Does.Contain("on"));
        Assert.That(cut.Find(".toggle-line").GetAttribute("aria-pressed"), Is.EqualTo("true"));
        var titles = cut.FindAll(".evt-title").Select(t => t.TextContent.Trim()).ToList();
        Assert.That(titles.Any(t => t.StartsWith("MVO Projected")), Is.True);

        cut.Find(".toggle-line").Click();

        Assert.That(_preferences.CausalityTechNamesWrites, Is.EqualTo(new[] { true, false }));
        Assert.That(cut.Find(".toggle-line").ClassList, Does.Not.Contain("on"));
    }

    [Test]
    public void FlowCardSelection_OpensTheDrawerWithTheEventAttributeRows()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".drawer"), Is.Empty);

        // Only the Export queued event carries attribute rows (its persisted CSO change snapshot)
        cut.Find(".evt-card.clickable").Click();

        Assert.That(cut.FindAll(".drawer"), Has.Count.EqualTo(1));
        Assert.That(cut.Find(".drawer-title").TextContent.Trim(), Is.EqualTo("Export queued"));
        Assert.That(AttributeRowCount(cut.FindAll(".drawer tbody tr")), Is.EqualTo(3));
    }

    [Test]
    public void DrawerCloseButton_ClearsTheDrawerAndTheCardSelection()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        cut.Find(".evt-card.clickable").Click();
        Assert.That(cut.FindAll(".drawer"), Has.Count.EqualTo(1));

        cut.Find(".drawer-close").Click();

        Assert.That(cut.FindAll(".drawer"), Is.Empty);
        Assert.That(cut.FindAll(".evt-card.selected"), Is.Empty);
    }

    [Test]
    public void ViewSwitcher_SelectingGraph_SwitchesTheViewAndPersistsThePreference()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        cut.FindAll(".seg button")[2].Click();

        Assert.That(cut.FindAll(".graph-svg"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".flow-cols"), Is.Empty);
        Assert.That(_preferences.CausalityViewWrites, Is.EqualTo(new[] { "graph" }));
    }

    [Test]
    public void GraphNodeSelection_AttributeBearingNode_OpensTheDrawerWithItsRows()
    {
        _preferences.StoredCausalityView = "graph";
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        Assert.That(cut.FindAll(".drawer"), Is.Empty);

        // Only the Export queued event carries attribute rows (its persisted CSO change snapshot)
        cut.FindAll(".g-node").Single(g => g.TextContent.Contains("3 attributes")).Click();

        Assert.That(cut.FindAll(".drawer"), Has.Count.EqualTo(1));
        Assert.That(cut.Find(".drawer-title").TextContent.Trim(), Is.EqualTo("Export queued"));
        Assert.That(AttributeRowCount(cut.FindAll(".drawer tbody tr")), Is.EqualTo(3));
    }

    [Test]
    public void GraphNodeSelection_NonAttributeNode_IsInertRatherThanSelectable()
    {
        _preferences.StoredCausalityView = "graph";
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        // The drawer is the only thing selection drives, so a node with no attribute rows must not
        // invite a click: selecting it would highlight the node and open nothing, which reads as the
        // click having failed.
        var node = cut.FindAll(".g-node").Single(g => g.TextContent.Contains("Identity created"));
        Assert.That(node.GetAttribute("role"), Is.Null);

        node.Click();

        Assert.That(cut.FindAll(".g-node.selected"), Is.Empty);
        Assert.That(cut.FindAll(".drawer"), Is.Empty);
    }

    [Test]
    public void SwitchingToTimeline_ClosesTheFlowDrawer()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        cut.Find(".evt-card.clickable").Click();
        Assert.That(cut.FindAll(".drawer"), Has.Count.EqualTo(1));

        cut.FindAll(".seg button")[1].Click();

        Assert.That(cut.FindAll(".drawer"), Is.Empty);
        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
    }

    [Test]
    public void TechnicalNamesToggle_AlsoRewordsTheSummarySentence()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        Assert.That(cut.Find(".summary-sentence").TextContent, Does.Contain("processed the record for"));

        cut.Find(".toggle-line").Click();

        // The toggle governs the whole panel, not just the views: the summary is the first sentence
        // read, and leaving "record" and "Identity" in it made the toggle look like it had not worked.
        var sentence = cut.Find(".summary-sentence").TextContent;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sentence, Does.Contain("processed the Connected System Object"));
            Assert.That(sentence, Does.Not.Contain("the record"));
        }
    }

    [Test]
    public void CausalChain_NotResolvedByThePage_RendersNoCausedBySection()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".caused-by"), Is.Empty);
    }

    [Test]
    public void CausalChain_Supplied_RendersBeneathTheCanvasAndReadsTheRecordAsTheEffect()
    {
        var item = CausalityTestData.NewJoinerItem();
        var chain = new CausalChain
        {
            RunProfileExecutionItemId = item.Id,
            Cohorts =
            [
                new CausalChainCohort
                {
                    EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                    ObjectTypeName = "User",
                    ObjectTypePluralName = "Users",
                    AttributeName = "Static Members",
                    Members =
                    [
                        new CausalChainMember
                        {
                            DisplayName = "Tina Adams",
                            Resolution = CausalChainResolution.NoFurtherCauses
                        }
                    ]
                }
            ]
        };

        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.Item, item)
            .Add(c => c.Context, CausalityTestData.NewJoinerContext())
            .Add(c => c.Chain, chain));

        // The record's display name, not its label: the label carries the external id in parentheses,
        // which reads as nonsense inside a possessive.
        Assert.That(cut.Find(".cb-sentence").TextContent.Trim(), Is.EqualTo(
            "Tina Adams was deleted, so they were removed from Liam Allen's Static Members"));
    }
}
