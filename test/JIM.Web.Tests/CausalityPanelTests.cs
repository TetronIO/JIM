// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityPanel"/>: rendering across the PRD scenarios, the view
/// switcher (Flow default, Timeline selectable, stored preferences honoured with graceful fallback
/// for not-yet-available views), the technical-names toggle persisting via a stubbed
/// <see cref="JIM.Web.Services.IUserPreferenceService"/>, the Flow drawer, and the empty
/// (not-tracked) state.
/// </summary>
[TestFixture]
public class CausalityPanelTests
{
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
        Assert.That(cut.FindAll("a[href='/admin/deleted-objects']"), Is.Not.Empty);
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
    public void Render_ViewSwitcher_ShowsFlowAndTimelineWithFlowOn()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var buttons = cut.FindAll(".seg button");
        Assert.That(buttons.Select(b => b.TextContent.Trim()), Is.EqualTo(new[] { "Flow", "Timeline" }));
        Assert.That(cut.FindAll(".seg button")[0].ClassList, Does.Contain("on"));
        Assert.That(cut.FindAll(".seg button")[1].ClassList, Does.Not.Contain("on"));
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
    public void Render_PersistedUnavailableViewPreference_FallsBackToFlowWithoutOverwritingIt()
    {
        _preferences.StoredCausalityView = "graph";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        // The Graph view does not exist yet, so the panel renders the default Flow view...
        Assert.That(cut.FindAll(".flow-cols"), Has.Count.EqualTo(1));
        // ...without clobbering the stored preference, so Graph is restored once it ships
        Assert.That(_preferences.CausalityViewWrites, Is.Empty);
        Assert.That(_preferences.StoredCausalityView, Is.EqualTo("graph"));
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
        Assert.That(cut.FindAll(".drawer .attr-row"), Has.Count.EqualTo(3));
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
    public void SwitchingToTimeline_ClosesTheFlowDrawer()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        cut.Find(".evt-card.clickable").Click();
        Assert.That(cut.FindAll(".drawer"), Has.Count.EqualTo(1));

        cut.FindAll(".seg button")[1].Click();

        Assert.That(cut.FindAll(".drawer"), Is.Empty);
        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
    }
}
