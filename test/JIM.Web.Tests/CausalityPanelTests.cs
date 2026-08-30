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
/// switcher (Lineage default; Timeline selectable; stored preferences honoured, with legacy Flow and
/// Graph values falling back silently), the technical-names toggle persisting via a stubbed
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
    public void Render_NewJoinerScenario_RendersSummaryBandAndLineageByDefault()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".summary-sentence"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".ln-canvas"), Has.Count.EqualTo(1));
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
        Assert.That(cut.FindAll(".ln-canvas"), Is.Empty);
    }

    [Test]
    public void Render_ViewSwitcher_OffersLineageAndTimelineWithLineageOn()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var buttons = cut.FindAll(".seg button");
        Assert.That(buttons.Select(b => b.TextContent.Trim()), Is.EqualTo(new[] { "Lineage", "Timeline" }));
        Assert.That(cut.FindAll(".seg button")[0].ClassList, Does.Contain("on"));
        Assert.That(cut.FindAll(".seg button")[1].ClassList, Does.Not.Contain("on"));
    }

    [Test]
    public void ViewSwitcher_SelectingTimeline_SwitchesTheViewAndPersistsThePreference()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        cut.FindAll(".seg button")[1].Click();

        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".ln-canvas"), Is.Empty);
        Assert.That(_preferences.CausalityViewWrites, Is.EqualTo(new[] { "timeline" }));
    }

    [Test]
    public void Render_PersistedTimelinePreference_StartsOnTheTimeline()
    {
        _preferences.StoredCausalityView = "timeline";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".ln-canvas"), Is.Empty);
        Assert.That(cut.FindAll(".seg button")[1].ClassList, Does.Contain("on"));
    }

    [Test]
    public void Render_PersistedLineagePreference_StartsOnTheLineageWithoutRewritingIt()
    {
        _preferences.StoredCausalityView = "lineage";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".ln-canvas"), Has.Count.EqualTo(1));
        Assert.That(_preferences.CausalityViewWrites, Is.Empty);
    }

    [TestCase("flow")]
    [TestCase("graph")]
    [TestCase("spine")]
    [TestCase("constellation")]
    public void Render_PersistedRetiredOrUnknownPreference_FallsBackToLineageWithoutOverwritingIt(string stored)
    {
        // Stored Flow and Graph preferences outlive their views, and "spine" outlives the name the
        // Lineage view shipped under in development (#1495); all must resolve to the default Lineage
        // without being clobbered, exactly as an unknown value always has.
        _preferences.StoredCausalityView = stored;

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".ln-canvas"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".seg button")[0].ClassList, Does.Contain("on"));
        Assert.That(_preferences.CausalityViewWrites, Is.Empty);
        Assert.That(_preferences.StoredCausalityView, Is.EqualTo(stored));
    }

    [Test]
    public void Render_PersistedTechNamesPreference_StartsWithTechnicalEmphasis()
    {
        _preferences.StoredCausalityTechNames = true;

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.Find(".toggle-line").ClassList, Does.Contain("on"));
        // Projected's own card head is suppressed on the Lineage (#1495 second follow-up: its title is
        // subsumed by the PROJECTED join label and the operation chip), so its technical label now
        // surfaces on the chip rather than on an .evt-title.
        var chips = cut.FindAll(".ln-op").Select(t => t.TextContent.Trim()).ToList();
        Assert.That(chips.Any(t => t.StartsWith("MVO Projected")), Is.True);
    }

    [Test]
    public void TechToggle_Click_PersistsViaThePreferenceServiceAndSwapsEmphasis()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        cut.Find(".toggle-line").Click();

        Assert.That(_preferences.CausalityTechNamesWrites, Is.EqualTo(new[] { true }));
        Assert.That(cut.Find(".toggle-line").ClassList, Does.Contain("on"));
        Assert.That(cut.Find(".toggle-line").GetAttribute("aria-pressed"), Is.EqualTo("true"));
        // See the comment above: Projected's technical label now shows on its chip, not its (suppressed) title.
        var chips = cut.FindAll(".ln-op").Select(t => t.TextContent.Trim()).ToList();
        Assert.That(chips.Any(t => t.StartsWith("MVO Projected")), Is.True);

        cut.Find(".toggle-line").Click();

        Assert.That(_preferences.CausalityTechNamesWrites, Is.EqualTo(new[] { true, false }));
        Assert.That(cut.Find(".toggle-line").ClassList, Does.Not.Contain("on"));
    }

    [Test]
    public void LineageCardSelection_OpensTheDrawerWithTheEventAttributeRows()
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
    public void SwitchingToTimeline_ClosesTheLineageDrawer()
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
    public void CausalChain_NotResolvedByThePage_RendersNoChainCards()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".ln-card"), Is.Empty);
    }

    [Test]
    public void CausalChain_Supplied_RendersOnTheLineageAndReadsTheRecordAsTheEffect()
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
        Assert.That(cut.Find(".ln-sentence").TextContent.Trim(), Is.EqualTo(
            "Tina Adams was deleted, so they were removed from Liam Allen's Static Members"));
    }
}
