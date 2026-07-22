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
/// bUnit tests for <see cref="CausalityPanel"/>: rendering across the PRD scenarios, persisted
/// preference loading via a stubbed <see cref="JIM.Web.Services.IUserPreferenceService"/>, the
/// technical-names toggle persisting, and the empty (not-tracked) state.
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
    public void Render_NewJoinerScenario_RendersSummaryBandAndTimeline()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".summary-sentence"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".oc-pill"), Is.Not.Empty);
    }

    [Test]
    public void Render_LeaverScenario_RendersWithoutException()
    {
        var cut = RenderPanel(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".tl-row").Count, Is.GreaterThan(1));
        Assert.That(cut.FindAll("a[href='/admin/deleted-objects']"), Is.Not.Empty);
    }

    [Test]
    public void Render_ExportFailureScenario_RendersWithoutException()
    {
        var cut = RenderPanel(CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext());

        Assert.That(cut.FindAll(".tl-row").Count, Is.GreaterThan(1));
        var badges = cut.FindAll(".evt-badge").Select(b => b.TextContent.Trim());
        Assert.That(badges, Does.Contain("Needs attention"));
    }

    [Test]
    public void Render_NoOutcomes_ShowsTheNotTrackedAlert()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };

        var cut = RenderPanel(item, CausalityTestData.NewJoinerContext());

        Assert.That(cut.Markup, Does.Contain("Outcome tracking was not enabled"));
        Assert.That(cut.FindAll(".tl"), Is.Empty);
    }

    [Test]
    public void Render_WithOnlyTimelineAvailable_HidesTheViewSwitcher()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.FindAll(".seg"), Is.Empty);
    }

    [Test]
    public void Render_PersistedTechNamesPreference_StartsWithTechnicalEmphasis()
    {
        _preferences.StoredCausalityTechNames = true;

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(cut.Find(".toggle-line").ClassList, Does.Contain("on"));
        var verbs = cut.FindAll(".tl-line .verb").Select(v => v.TextContent.Trim()).ToList();
        Assert.That(verbs, Does.Contain("MVO Projected"));
    }

    [Test]
    public void Render_PersistedUnavailableViewPreference_FallsBackToTimelineWithoutOverwritingIt()
    {
        _preferences.StoredCausalityView = "flow";

        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        // The Flow view does not exist yet, so the panel renders the Timeline...
        Assert.That(cut.FindAll(".tl"), Has.Count.EqualTo(1));
        // ...without clobbering the stored preference, so Flow is restored once it ships
        Assert.That(_preferences.CausalityViewWrites, Is.Empty);
        Assert.That(_preferences.StoredCausalityView, Is.EqualTo("flow"));
    }

    [Test]
    public void TechToggle_Click_PersistsViaThePreferenceServiceAndSwapsEmphasis()
    {
        var cut = RenderPanel(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        cut.Find(".toggle-line").Click();

        Assert.That(_preferences.CausalityTechNamesWrites, Is.EqualTo(new[] { true }));
        Assert.That(cut.Find(".toggle-line").ClassList, Does.Contain("on"));
        Assert.That(cut.Find(".toggle-line").GetAttribute("aria-pressed"), Is.EqualTo("true"));
        var verbs = cut.FindAll(".tl-line .verb").Select(v => v.TextContent.Trim()).ToList();
        Assert.That(verbs, Does.Contain("MVO Projected"));

        cut.Find(".toggle-line").Click();

        Assert.That(_preferences.CausalityTechNamesWrites, Is.EqualTo(new[] { true, false }));
        Assert.That(cut.Find(".toggle-line").ClassList, Does.Not.Contain("on"));
    }
}
