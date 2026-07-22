// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityEventCard"/>: the Flow event card's emphasis swap, badge,
/// entity chips, footer (attribute count and action links) and keyboard-operable selection.
/// </summary>
[TestFixture]
public class CausalityEventCardTests
{
    private static CausalityEvent FindEvent(
        CausalityModel model, ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return model.AllEvents().First(e => e.OutcomeType == outcomeType);
    }

    private static IRenderedComponent<CausalityEventCard> RenderCard(
        BunitContext context,
        CausalityEvent causalityEvent,
        bool technicalNames = false,
        bool selected = false,
        Microsoft.AspNetCore.Components.EventCallback<CausalityEvent> onSelect = default)
    {
        return context.Render<CausalityEventCard>(ps => ps
            .Add(c => c.Event, causalityEvent)
            .Add(c => c.TechnicalNames, technicalNames)
            .Add(c => c.Selected, selected)
            .Add(c => c.OnSelect, onSelect));
    }

    [Test]
    public async Task Render_PlainNames_EmphasisesPlainTitleWithTechnicalDemotedAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var projected = FindEvent(model, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        var cut = RenderCard(context, projected);

        var title = cut.Find(".evt-title");
        Assert.That(title.TextContent.Trim(), Does.StartWith("Identity created"));
        Assert.That(title.QuerySelector(".tech")!.TextContent, Does.Contain("MVO Projected"));
    }

    [Test]
    public async Task Render_TechnicalNames_SwapsTheEmphasisAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var projected = FindEvent(model, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        var cut = RenderCard(context, projected, technicalNames: true);

        var title = cut.Find(".evt-title");
        Assert.That(title.TextContent.Trim(), Does.StartWith("MVO Projected"));
        Assert.That(title.QuerySelector(".tech")!.TextContent, Does.Contain("Identity created"));
    }

    [Test]
    public async Task Render_EventWithAttributes_IsClickableAndKeyboardOperableAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var pendingExport = FindEvent(model, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);
        CausalityEvent? selectedEvent = null;
        var selectCount = 0;
        var onSelect = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<CausalityEvent>(
            this, e => { selectedEvent = e; selectCount++; });

        var cut = RenderCard(context, pendingExport, onSelect: onSelect);

        var card = cut.Find(".evt-card");
        Assert.That(card.ClassList, Does.Contain("clickable"));
        Assert.That(card.GetAttribute("role"), Is.EqualTo("button"));
        Assert.That(card.GetAttribute("tabindex"), Is.EqualTo("0"));

        card.Click();
        Assert.That(selectCount, Is.EqualTo(1));
        Assert.That(selectedEvent, Is.SameAs(pendingExport));

        card.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.That(selectCount, Is.EqualTo(2));

        card.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = " " });
        Assert.That(selectCount, Is.EqualTo(3));

        // Other keys must not select
        card.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Tab" });
        Assert.That(selectCount, Is.EqualTo(3));
    }

    [Test]
    public async Task Render_EventWithoutAttributes_IsNotClickableAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var projected = FindEvent(model, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        var onSelect = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<CausalityEvent>(this, _ => { });

        var cut = RenderCard(context, projected, onSelect: onSelect);

        var card = cut.Find(".evt-card");
        Assert.That(card.ClassList, Does.Not.Contain("clickable"));
        Assert.That(card.HasAttribute("role"), Is.False);
        Assert.That(card.HasAttribute("tabindex"), Is.False);
    }

    [Test]
    public async Task Render_SelectedCard_CarriesTheSelectedClassAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var pendingExport = FindEvent(model, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);

        var cut = RenderCard(context, pendingExport, selected: true);

        Assert.That(cut.Find(".evt-card").ClassList, Does.Contain("selected"));
    }

    [Test]
    public async Task Render_DestructiveEvent_ShowsTheBadgeAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var deleted = FindEvent(model, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);

        var cut = RenderCard(context, deleted);

        Assert.That(cut.Find(".evt-badge").TextContent.Trim(), Is.EqualTo("Destructive"));
    }

    [Test]
    public async Task Render_Footer_ShowsAttributeCountAndUpToTwoActionLinksAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var pendingExport = FindEvent(model, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);

        var cut = RenderCard(context, pendingExport);

        Assert.That(cut.Find(".evt-foot .attr-count").TextContent, Does.Contain("3 attributes"));
        var actionLinks = cut.FindAll(".evt-foot a").Select(a => a.TextContent.Trim()).ToList();
        Assert.That(actionLinks, Has.Count.LessThanOrEqualTo(2));
        Assert.That(actionLinks, Does.Contain("Pending Exports"));
    }

    [Test]
    public async Task Render_EventWithEntities_RendersEntityChipsAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var projected = FindEvent(model, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        var cut = RenderCard(context, projected);

        // Projected carries the Identity link and the Synchronisation Rule attribution as chips
        var chips = cut.FindAll(".evt-entities .chip").Select(c => c.TextContent).ToList();
        Assert.That(chips.Any(c => c.Contains("Liam Allen")), Is.True);
        Assert.That(chips.Any(c => c.Contains("Yellowstone People - Inbound")), Is.True);
    }
}
