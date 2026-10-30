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
using JIM.Web.Shared;
using JIM.Web.Shared.Causality;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityPanel"/>: rendering across the PRD scenarios, the view
/// switcher (Lineage default; Timeline selectable; stored preferences honoured, with legacy Flow and
/// Graph values falling back silently), the shared attribute drawer, and the empty (not-tracked)
/// state. The panel names every outcome once, in the portal's own vocabulary, and carries no
/// "Technical names" toggle.
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
        Assert.That(cut.FindAll(".outcome-strip .mud-chip"), Is.Not.Empty);
    }

    /// <summary>
    /// The item page's panel offers the same Lineage in the Causality Explorer (#2100), where it can be followed in place;
    /// the explorer itself, already there, does not offer it.
    /// </summary>
    [Test]
    public void Render_ExplorerAddressGiven_OffersToOpenTheLineageInTheExplorer()
    {
        var item = CausalityTestData.NewJoinerItem();

        var withAddress = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.Item, item)
            .Add(c => c.Context, CausalityTestData.NewJoinerContext())
            .Add(c => c.ExplorerHref, $"/causality/items/{item.Id}"));
        var withoutAddress = RenderPanel(item, CausalityTestData.NewJoinerContext());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withAddress.Find("a.jim-causality-open-explorer").GetAttribute("href"), Is.EqualTo($"/causality/items/{item.Id}"));
            Assert.That(withAddress.Find("a.jim-causality-open-explorer").TextContent.Trim(), Is.EqualTo("Open in explorer"));
            Assert.That(withoutAddress.FindAll("a.jim-causality-open-explorer"), Is.Empty);
        }
    }

    /// <summary>
    /// The Causality Explorer shows the Lineage alone (#2100): its own header already says what is in view, and the
    /// Timeline and Table stay on the item page. A stored preference for another view must not pull it away either.
    /// </summary>
    [Test]
    public void Render_LineageOnly_ShowsTheLineageWithNoBandOrViewSwitcher()
    {
        _preferences.StoredCausalityView = "timeline";

        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.Item, CausalityTestData.NewJoinerItem())
            .Add(c => c.Context, CausalityTestData.NewJoinerContext())
            .Add(c => c.LineageOnly, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".ln-canvas"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(".summary-sentence"), Is.Empty);
            Assert.That(cut.HasComponent<SegmentedToggle<CausalityView>>(), Is.False);
            Assert.That(cut.FindAll(".tl"), Is.Empty);
            Assert.That(_preferences.CausalityViewWrites, Is.Empty);
        }
    }

    [Test]
    public void Render_ConsequencesArrivingAfterTheFirstRender_AddAfterThisRunToTheLineageAndTheBand()
    {
        // The page reads the walk after the panel is on screen (#1811), so its arrival is a parameter change the panel
        // must pick up: the After this run sections in the Lineage, and the strip in the band.
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ObjectChangeType = JIM.Models.Enums.ObjectChangeType.Updated };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, parent: null, ordinal: 0, detailCount: 1);
        var context = CausalityConsequenceTestData.ImportContext();
        var cut = _context.Render<CausalityPanel>(ps => ps
            .Add(c => c.Item, item)
            .Add(c => c.Context, context)
            .Add(c => c.ConsequencesLoading, true));
        Assert.That(cut.FindAll(".ln-consequences-loading"), Has.Count.EqualTo(1));

        cut.Render(ps => ps
            .Add(c => c.ConsequencesLoading, false)
            .Add(c => c.Consequences, CausalityConsequenceTestData.CompleteCascade()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".ln-after"), Is.Not.Empty);
            Assert.That(cut.Find(".after-strip").TextContent, Does.Contain("Metaverse Object deleted"));
            Assert.That(cut.FindAll(".ln-consequences-loading"), Is.Empty);
        }
    }

    [Test]
    public void Render_ExportHeldForADecision_RendersTheSummaryBandRatherThanTheNoTrackingNotice()
    {
        // A held export (#242, release 4) records no outcome because nothing was exported; the band says why.
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ErrorType = ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved };

        var cut = RenderPanel(item, CausalityTestData.ExportContext());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".summary-sentence"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(".outcome-strip .mud-chip"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Not.Contain("Outcome tracking was not enabled"));
        }
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
    public void Render_ViewSwitcher_OffersLineageTimelineAndTableWithLineageOn()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var buttons = cut.FindAll(".seg button");
        Assert.That(buttons.Select(b => b.TextContent.Trim()), Is.EqualTo(new[] { "Lineage", "Timeline", "Table" }));
        Assert.That(cut.FindAll(".seg button")[0].ClassList, Does.Contain("on"));
        Assert.That(cut.FindAll(".seg button")[1].ClassList, Does.Not.Contain("on"));
        Assert.That(cut.FindAll(".seg button")[2].ClassList, Does.Not.Contain("on"));
    }

    [Test]
    public async Task ViewSwitcher_SelectingTable_SwitchesToTheTableViewAndPersistsThePreferenceAsync()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        await cut.FindAll(".seg button")[2].ClickAsync(new MouseEventArgs());

        Assert.That(cut.FindAll(".tv"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".ln-canvas"), Is.Empty);
        Assert.That(_preferences.CausalityViewWrites, Is.EqualTo(new[] { "table" }));
    }

    [Test]
    public async Task ViewSwitcher_SelectingTimeline_SwitchesTheViewAndPersistsThePreferenceAsync()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        await cut.FindAll(".seg button")[1].ClickAsync(new MouseEventArgs());

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
    public void Render_NoTechnicalNamesToggle_RendersNoSuchElementOrText()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".toggle-line"), Is.Empty);
        Assert.That(cut.Markup, Does.Not.Contain("Technical names"));
    }

    [Test]
    public async Task LineageCardSelection_OpensTheDrawerWithTheEventAttributeRowsAsync()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".drawer"), Is.Empty);

        // Only the Export queued event carries attribute rows (its persisted CSO change snapshot)
        await cut.Find(".evt-card.clickable").ClickAsync(new MouseEventArgs());

        Assert.That(cut.FindAll(".drawer"), Has.Count.EqualTo(1));
        Assert.That(cut.Find(".drawer-title").TextContent.Trim(), Is.EqualTo("Export queued"));
        Assert.That(AttributeRowCount(cut.FindAll(".drawer tbody tr")), Is.EqualTo(3));
    }

    [Test]
    public async Task DrawerCloseButton_ClearsTheDrawerAndTheCardSelectionAsync()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        await cut.Find(".evt-card.clickable").ClickAsync(new MouseEventArgs());
        Assert.That(cut.FindAll(".drawer"), Has.Count.EqualTo(1));

        await cut.Find(".drawer-close").ClickAsync(new MouseEventArgs());

        Assert.That(cut.FindAll(".drawer"), Is.Empty);
        Assert.That(cut.FindAll(".evt-card.selected"), Is.Empty);
    }

    [Test]
    public async Task SwitchingToTimeline_ClosesTheLineageDrawerAsync()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        await cut.Find(".evt-card.clickable").ClickAsync(new MouseEventArgs());
        Assert.That(cut.FindAll(".drawer"), Has.Count.EqualTo(1));

        await cut.FindAll(".seg button")[1].ClickAsync(new MouseEventArgs());

        Assert.That(cut.FindAll(".drawer"), Is.Empty);
        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
    }

    [Test]
    public void Render_SummarySentence_NamesTheObjectTypeAndNeverSaysRecordOrIdentity()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var sentence = cut.Find(".summary-sentence").TextContent;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sentence, Does.Contain("processed person Liam Allen"));
            Assert.That(sentence, Does.Not.Contain("record"));
            Assert.That(sentence, Does.Not.Contain("Identity"));
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
