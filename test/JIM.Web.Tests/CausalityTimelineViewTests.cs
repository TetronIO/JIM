// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityTimelineView"/>: event order and nesting, the
/// plain-vs-technical emphasis swap, deletion-record linking and inline attribute expansion.
/// </summary>
[TestFixture]
public class CausalityTimelineViewTests
{
    private static IRenderedComponent<CausalityTimelineView> RenderTimeline(
        BunitContext context,
        CausalityModel model,
        bool technicalNames = false)
    {
        return context.Render<CausalityTimelineView>(ps => ps
            .Add(c => c.Model, model)
            .Add(c => c.TechnicalNames, technicalNames));
    }

    [Test]
    public async Task Render_NewJoinerScenario_RendersSourceRowThenEventsInOrderAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        var verbs = cut.FindAll(".tl-line .verb").Select(v => v.TextContent.Trim()).ToList();
        Assert.That(verbs, Is.EqualTo(new[]
        {
            "Record processed", "Identity created", "Attributes flowed", "Provisioned", "Export queued"
        }));
    }

    [Test]
    public async Task Render_NewJoinerScenario_NestsChildEventsUnderTheirParentsAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        // Projected > Attributes flowed > Provisioned > Export queued: three nested child containers
        var deepestRows = cut.FindAll(".tl-children .tl-children .tl-children .tl-row");
        Assert.That(deepestRows, Has.Count.EqualTo(1));
        Assert.That(deepestRows[0].QuerySelector(".verb")!.TextContent.Trim(), Is.EqualTo("Export queued"));
    }

    [Test]
    public async Task Render_PlainNames_EmphasisesPlainLabelWithTechnicalDemotedAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model, technicalNames: false);

        var projectedRow = cut.FindAll(".tl-row")[1];
        Assert.That(projectedRow.QuerySelector(".verb")!.TextContent.Trim(), Is.EqualTo("Identity created"));
        Assert.That(projectedRow.QuerySelector(".tech")!.TextContent, Does.Contain("MVO Projected"));
    }

    [Test]
    public async Task Render_TechnicalNames_SwapsTheEmphasisAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model, technicalNames: true);

        var projectedRow = cut.FindAll(".tl-row")[1];
        Assert.That(projectedRow.QuerySelector(".verb")!.TextContent.Trim(), Is.EqualTo("MVO Projected"));
        Assert.That(projectedRow.QuerySelector(".tech")!.TextContent, Does.Contain("Identity created"));
    }

    [Test]
    public async Task Render_LeaverScenario_MvoDeletedRowLinksToTheDeletionRecordBrowserAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        var deletionLinks = cut.FindAll("a[href='/admin/deleted-objects']");
        Assert.That(deletionLinks, Is.Not.Empty);
    }

    [Test]
    public async Task Render_LeaverScenario_RendersTheDestructiveBadgeAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        var badges = cut.FindAll(".evt-badge").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(badges, Does.Contain("Destructive"));
    }

    [Test]
    public async Task Expander_Click_TogglesTheInlineAttributeDetailAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        // Host with a real @bind-ExpandedEvent so the toggle round-trips like it does in the panel
        var cut = context.Render<TimelineHost>(ps => ps.Add(c => c.Model, model));

        // Only the Export queued event carries attribute rows (its persisted CSO change snapshot)
        var expanders = cut.FindAll(".tl-expander");
        Assert.That(expanders, Has.Count.EqualTo(1));
        Assert.That(expanders[0].TextContent, Does.Contain("3 attributes"));
        Assert.That(cut.FindAll(".tl-inline-detail"), Is.Empty);

        cut.Find(".tl-expander").Click();

        Assert.That(cut.FindAll(".tl-inline-detail"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".attr-row"), Has.Count.EqualTo(3));
        Assert.That(cut.Find(".tl-expander").ClassList, Does.Contain("open"));

        cut.Find(".tl-expander").Click();

        Assert.That(cut.FindAll(".tl-inline-detail"), Is.Empty);
    }

    /// <summary>
    /// Hosts the Timeline with owned expanded-event state, mirroring how CausalityPanel binds it.
    /// </summary>
    private sealed class TimelineHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter]
        public CausalityModel Model { get; set; } = null!;

        private CausalityEvent? _expandedEvent;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<CausalityTimelineView>(0);
            builder.AddComponentParameter(1, nameof(CausalityTimelineView.Model), Model);
            builder.AddComponentParameter(2, nameof(CausalityTimelineView.ExpandedEvent), _expandedEvent);
            builder.AddComponentParameter(3, nameof(CausalityTimelineView.ExpandedEventChanged),
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<CausalityEvent?>(
                    this, value => _expandedEvent = value));
            builder.CloseComponent();
        }
    }
}
