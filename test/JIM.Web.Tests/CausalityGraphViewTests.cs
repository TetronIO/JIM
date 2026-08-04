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
/// bUnit tests for <see cref="CausalityGraphView"/>: node and edge rendering from the computed
/// layout, selection through the two-way binding (click and keyboard), the technical-names title
/// swap, the inert rendering of nodes the drawer cannot open for (the synthetic source root and any
/// event without attribute rows), and the tone legend.
/// </summary>
[TestFixture]
public class CausalityGraphViewTests
{
    private static IRenderedComponent<CausalityGraphView> RenderGraph(
        BunitContext context, CausalityModel model, bool technicalNames = false)
    {
        return context.Render<CausalityGraphView>(ps => ps
            .Add(c => c.Model, model)
            .Add(c => c.TechnicalNames, technicalNames));
    }

    private static CausalityModel NewJoinerModel()
    {
        return CausalityModelBuilder.Build(
            CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
    }

    [Test]
    public async Task Render_NewJoinerScenario_RendersOneNodePerEventPlusTheSourceRootAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderGraph(context, NewJoinerModel());

        Assert.That(cut.FindAll(".graph-svg .g-node"), Has.Count.EqualTo(5));
        Assert.That(cut.FindAll(".graph-svg .edge"), Has.Count.EqualTo(4));

        // The SVG canvas carries the computed layout dimensions
        var svg = cut.Find(".graph-svg");
        Assert.That(svg.GetAttribute("width"), Is.EqualTo("1684"));
        Assert.That(svg.GetAttribute("height"), Is.EqualTo("62"));
        Assert.That(svg.GetAttribute("viewBox"), Is.EqualTo("0 0 1684 62"));
    }

    [Test]
    public async Task Render_NewJoinerScenario_RendersNodeTitlesAndSubsAsSvgTextAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderGraph(context, NewJoinerModel());

        var titles = cut.FindAll(".g-node text:not(.sub)").Select(t => t.TextContent.Trim()).ToList();
        Assert.That(titles, Does.Contain("Identity created"));
        Assert.That(titles, Does.Contain("Export queued"));
        Assert.That(titles, Does.Contain("Source record"));

        var subs = cut.FindAll(".g-node text.sub").Select(t => t.TextContent.Trim()).ToList();
        Assert.That(subs, Does.Contain("3 attributes"));
        Assert.That(subs, Does.Contain("Liam Allen (S8-287551)"));
    }

    [Test]
    public async Task Render_SelectableNodes_AreKeyboardOperableButtonsWhileTheSourceRootIsStaticAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderGraph(context, NewJoinerModel());

        var interactiveNodes = cut.FindAll(".g-node[role=button]");
        Assert.That(interactiveNodes, Is.Not.Empty);
        Assert.That(interactiveNodes.Select(n => n.GetAttribute("tabindex")), Is.All.EqualTo("0"));

        var sourceNode = cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Source record"));
        Assert.That(sourceNode.ClassList, Does.Contain("static"));
        Assert.That(sourceNode.GetAttribute("tabindex"), Is.Null);
        Assert.That(sourceNode.GetAttribute("role"), Is.Null);
    }

    [Test]
    public async Task NodeClick_TogglesTheSelectedEventThroughTheBindingAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var cut = context.Render<GraphHost>(ps => ps.Add(c => c.Model, NewJoinerModel()));

        var node = cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Export queued"));
        node.Click();

        Assert.That(cut.FindAll(".g-node.selected"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Export queued")).ClassList,
            Does.Contain("selected"));

        // Selecting the same node again clears the selection
        cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Export queued")).Click();
        Assert.That(cut.FindAll(".g-node.selected"), Is.Empty);
    }

    [Test]
    public async Task Render_EventNodeWithoutAttributeDetail_IsStaticNotAButtonAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderGraph(context, NewJoinerModel());

        // The drawer only opens for an event carrying attribute rows, and in this model only Export
        // queued has any, so it is the sole node entitled to present itself as a button. A node whose
        // click could do nothing must not advertise one (the rule CausalityEventCard already applies).
        var interactiveNodes = cut.FindAll(".g-node[role=button]");
        Assert.That(interactiveNodes, Has.Count.EqualTo(1));
        Assert.That(interactiveNodes.Single().TextContent, Does.Contain("Export queued"));

        var identityNode = cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Identity created"));
        Assert.That(identityNode.ClassList, Does.Contain("static"));
        Assert.That(identityNode.GetAttribute("role"), Is.Null);
        Assert.That(identityNode.GetAttribute("tabindex"), Is.Null);
    }

    [Test]
    public async Task NodeClick_OnAnEventWithoutAttributeDetail_LeavesTheSelectionUntouchedAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var cut = context.Render<GraphHost>(ps => ps.Add(c => c.Model, NewJoinerModel()));

        cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Identity created")).Click();

        Assert.That(cut.FindAll(".g-node.selected"), Is.Empty);
    }

    [Test]
    public async Task NodeKeyboardActivation_EnterAndSpace_SelectTheNodeAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var cut = context.Render<GraphHost>(ps => ps.Add(c => c.Model, NewJoinerModel()));

        var node = cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Export queued"));
        node.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.That(cut.FindAll(".g-node.selected"), Has.Count.EqualTo(1));

        cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Export queued"))
            .KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = " " });
        Assert.That(cut.FindAll(".g-node.selected"), Is.Empty);

        // Other keys must not change the selection
        cut.FindAll(".g-node").Single(n => n.TextContent.Contains("Export queued"))
            .KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Tab" });
        Assert.That(cut.FindAll(".g-node.selected"), Is.Empty);
    }

    [Test]
    public async Task Render_TechnicalNames_SwapsNodeTitlesAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderGraph(context, NewJoinerModel(), technicalNames: true);

        var titles = cut.FindAll(".g-node text:not(.sub)").Select(t => t.TextContent.Trim()).ToList();
        Assert.That(titles, Does.Contain("MVO Projected"));
        Assert.That(titles, Does.Not.Contain("Identity created"));
        // The synthetic source root keeps its plain title in both modes
        Assert.That(titles, Does.Contain("Source record"));
    }

    [Test]
    public async Task Render_Legend_ShowsTheFiveToneEntriesAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderGraph(context, NewJoinerModel());

        var entries = cut.FindAll(".g-legend span").Select(s => s.TextContent.Trim()).ToList();
        Assert.That(entries, Is.EqualTo(new[]
        {
            "Identity change", "Queued for export", "Scope & policy", "Destructive & failed", "Data flow"
        }));
        Assert.That(cut.FindAll(".g-legend i"), Has.Count.EqualTo(5));
    }

    /// <summary>
    /// Hosts the Graph view with owned selected-event state, mirroring how CausalityPanel binds it.
    /// </summary>
    private sealed class GraphHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter]
        public CausalityModel Model { get; set; } = null!;

        private CausalityEvent? _selectedEvent;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<CausalityGraphView>(0);
            builder.AddComponentParameter(1, nameof(CausalityGraphView.Model), Model);
            builder.AddComponentParameter(2, nameof(CausalityGraphView.SelectedEvent), _selectedEvent);
            builder.AddComponentParameter(3, nameof(CausalityGraphView.SelectedEventChanged),
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<CausalityEvent?>(
                    this, value => _selectedEvent = value));
            builder.CloseComponent();
        }
    }
}
