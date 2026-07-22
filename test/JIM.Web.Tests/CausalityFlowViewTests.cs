// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityFlowView"/>: lane/column placement across the PRD scenarios,
/// per-system downstream grouping, selection through the two-way binding, the technical-names
/// emphasis swap, and the connector overlay (rendered from stubbed measurements; degrading to no
/// connectors when the measurement interop fails).
/// </summary>
[TestFixture]
public class CausalityFlowViewTests
{
    private static IRenderedComponent<CausalityFlowView> RenderFlow(
        BunitContext context, CausalityModel model, bool technicalNames = false)
    {
        return context.Render<CausalityFlowView>(ps => ps
            .Add(c => c.Model, model)
            .Add(c => c.TechnicalNames, technicalNames));
    }

    private static string[] CardTitles(AngleSharp.Dom.IElement column)
    {
        return column.QuerySelectorAll(".evt-title").Select(t => t.TextContent.Trim()).ToArray();
    }

    [Test]
    public async Task Render_NewJoinerScenario_PlacesEventsInTheirLanesAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderFlow(context, model);

        var columns = cut.FindAll(".flow-col");
        Assert.That(columns, Has.Count.EqualTo(3));

        // Source column: the synthetic source record card with record + Connected System chips
        var sourceCards = columns[0].QuerySelectorAll(".evt-card");
        Assert.That(sourceCards, Has.Length.EqualTo(1));
        Assert.That(sourceCards[0].GetAttribute("data-flow-id"), Is.EqualTo("src"));
        var sourceChips = sourceCards[0].QuerySelectorAll(".chip").Select(c => c.TextContent).ToList();
        Assert.That(sourceChips.Any(c => c.Contains("Liam Allen")), Is.True);
        Assert.That(sourceChips.Any(c => c.Contains("Yellowstone APAC")), Is.True);

        // Identity column: the Identity-lane events flattened in tree order
        Assert.That(CardTitles(columns[1]), Has.Length.EqualTo(2));
        Assert.That(CardTitles(columns[1])[0], Does.StartWith("Identity created"));
        Assert.That(CardTitles(columns[1])[1], Does.StartWith("Attributes flowed"));

        // Downstream column: one Connected System group holding both downstream events
        var groups = columns[2].QuerySelectorAll(".sys-group");
        Assert.That(groups, Has.Length.EqualTo(1));
        Assert.That(groups[0].QuerySelector(".sys-group-head")!.TextContent, Does.Contain("Glitterband EMEA"));
        var groupTitles = groups[0].QuerySelectorAll(".evt-title").Select(t => t.TextContent.Trim()).ToArray();
        Assert.That(groupTitles[0], Does.StartWith("Provisioned"));
        Assert.That(groupTitles[1], Does.StartWith("Export queued"));
    }

    [Test]
    public async Task Render_LeaverScenario_GroupsDownstreamEventsPerConnectedSystemAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderFlow(context, model);

        var columns = cut.FindAll(".flow-col");

        // Identity column: Left scope, Attributes flowed and Identity deleted
        var identityTitles = CardTitles(columns[1]);
        Assert.That(identityTitles.Any(t => t.StartsWith("Left scope")), Is.True);
        Assert.That(identityTitles.Any(t => t.StartsWith("Identity deleted")), Is.True);

        // Downstream column: one group per Connected System, in first-appearance order
        var groupHeads = columns[2].QuerySelectorAll(".sys-group .sys-group-head")
            .Select(h => h.TextContent.Trim()).ToArray();
        Assert.That(groupHeads, Has.Length.EqualTo(2));
        Assert.That(groupHeads[0], Does.Contain("Glitterband EMEA"));
        Assert.That(groupHeads[1], Does.Contain("Contoso AD"));

        // Each group holds its own Export queued event
        var groups = cut.FindAll(".sys-group");
        Assert.That(groups[0].QuerySelectorAll(".evt-card"), Has.Length.EqualTo(1));
        Assert.That(groups[1].QuerySelectorAll(".evt-card"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Render_ExportFailureScenario_ShowsEmptyIdentityLaneAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext());

        var cut = RenderFlow(context, model);

        var columns = cut.FindAll(".flow-col");

        Assert.That(columns[1].TextContent, Does.Contain("No Identity changes."));
        Assert.That(columns[1].QuerySelectorAll(".evt-card"), Is.Empty);

        // Downstream: one group for the page's Connected System with both export events
        var groups = columns[2].QuerySelectorAll(".sys-group");
        Assert.That(groups, Has.Length.EqualTo(1));
        Assert.That(groups[0].QuerySelector(".sys-group-head")!.TextContent, Does.Contain("Glitterband EMEA"));
        var titles = groups[0].QuerySelectorAll(".evt-title").Select(t => t.TextContent.Trim()).ToArray();
        Assert.That(titles[0], Does.StartWith("Exported"));
        Assert.That(titles[1], Does.StartWith("Export failed"));

        // The connector error carried in DetailMessage is shown on the failed card
        Assert.That(groups[0].TextContent, Does.Contain("LDAP error 50: insufficient access rights"));
    }

    [Test]
    public async Task Render_NoDownstreamEvents_ShowsTheDownstreamEmptyStateAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var item = new JIM.Models.Activities.ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item,
            JIM.Models.Activities.ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.MvoId,
            targetEntityDescription: "Liam Allen");
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var cut = RenderFlow(context, model);

        Assert.That(cut.FindAll(".flow-col")[2].TextContent, Does.Contain("No downstream consequences."));
        Assert.That(cut.FindAll(".sys-group"), Is.Empty);
    }

    [Test]
    public async Task Render_TechnicalNames_SwapsEmphasisOnCardsAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderFlow(context, model, technicalNames: true);

        var identityTitles = CardTitles(cut.FindAll(".flow-col")[1]);
        Assert.That(identityTitles[0], Does.StartWith("MVO Projected"));
        Assert.That(identityTitles[0], Does.Contain("Identity created"));
    }

    [Test]
    public async Task CardSelection_TogglesTheSelectedEventThroughTheBindingAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = context.Render<FlowHost>(ps => ps.Add(c => c.Model, model));

        // Only the Export queued event carries attribute rows, so exactly one card is clickable
        var clickableCards = cut.FindAll(".evt-card.clickable");
        Assert.That(clickableCards, Has.Count.EqualTo(1));

        cut.Find(".evt-card.clickable").Click();
        Assert.That(cut.Find(".evt-card.clickable").ClassList, Does.Contain("selected"));

        // Selecting the same card again clears the selection
        cut.Find(".evt-card.clickable").Click();
        Assert.That(cut.Find(".evt-card.clickable").ClassList, Does.Not.Contain("selected"));
    }

    [Test]
    public async Task Render_MeasureInteropFailure_RendersWithoutConnectorsAndWithoutExceptionAsync()
    {
        await using var context = CausalityBunitContext.Create();
        context.JSInterop.Setup<CausalityFlowMeasurements?>("jimCausality.measure", _ => true)
            .SetException(new InvalidOperationException("JS interop is not available"));
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderFlow(context, model);

        // The columns render in full; the decorative connector overlay stays empty
        Assert.That(cut.FindAll(".flow-col"), Has.Count.EqualTo(3));
        Assert.That(cut.FindAll(".flow-svg path"), Is.Empty);
    }

    [Test]
    public async Task Render_WithMeasurements_RendersConnectorPathsAndTerminalDotsAsync()
    {
        await using var context = CausalityBunitContext.Create();
        context.JSInterop.Setup<CausalityFlowMeasurements?>("jimCausality.measure", _ => true)
            .SetResult(new CausalityFlowMeasurements
            {
                Width = 900,
                Height = 400,
                Cards =
                [
                    new CausalityFlowCardRect { Id = "src", Left = 0, Right = 200, Top = 0, Height = 100 },
                    new CausalityFlowCardRect { Id = "evt-0", Left = 300, Right = 500, Top = 0, Height = 60 },
                    new CausalityFlowCardRect { Id = "evt-1", Left = 300, Right = 500, Top = 80, Height = 60 },
                    new CausalityFlowCardRect { Id = "sys-0", Left = 600, Right = 800, Top = 0, Height = 200 }
                ]
            });
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderFlow(context, model);

        // Two connectors: src -> first Identity event, deepest Identity event -> the system group
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".flow-svg path"), Has.Count.EqualTo(2));
            Assert.That(cut.FindAll(".flow-svg circle"), Has.Count.EqualTo(2));
        });
        Assert.That(cut.Find(".flow-svg").GetAttribute("viewBox"), Is.EqualTo("0 0 900 400"));
    }

    /// <summary>
    /// Hosts the Flow view with owned selected-event state, mirroring how CausalityPanel binds it.
    /// </summary>
    private sealed class FlowHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter]
        public CausalityModel Model { get; set; } = null!;

        private CausalityEvent? _selectedEvent;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<CausalityFlowView>(0);
            builder.AddComponentParameter(1, nameof(CausalityFlowView.Model), Model);
            builder.AddComponentParameter(2, nameof(CausalityFlowView.SelectedEvent), _selectedEvent);
            builder.AddComponentParameter(3, nameof(CausalityFlowView.SelectedEventChanged),
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<CausalityEvent?>(
                    this, value => _selectedEvent = value));
            builder.CloseComponent();
        }
    }
}
