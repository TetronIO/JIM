// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Web;
using JIM.Web.Causality;
using JIM.Web.Shared;
using JIM.Web.Shared.Causality;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityTimelineView"/>: event order and nesting, the portal's one
/// vocabulary per outcome, deletion-record linking and inline attribute expansion.
/// </summary>
[TestFixture]
public class CausalityTimelineViewTests
{
    /// <summary>
    /// How many attribute rows a rendered detail table is actually showing. The rows live in a virtualised grid,
    /// which brackets them with two empty spacer rows (that is how a virtualiser reserves the height of what it
    /// has not rendered), so counting every row in the body counts two that carry nothing.
    /// </summary>
    private static int AttributeRowCount(IReadOnlyList<IElement> rows) =>
        rows.Count(row => row.Children.Length > 0);

    private static IRenderedComponent<CausalityTimelineView> RenderTimeline(
        BunitContext context,
        CausalityModel model)
    {
        return context.Render<CausalityTimelineView>(ps => ps
            .Add(c => c.Model, model));
    }

    [Test]
    public async Task Render_OpeningVerb_NamesTheConnectedSystemObjectAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        Assert.That(cut.FindAll(".verb")[0].TextContent.Trim(), Is.EqualTo("Connected System Object processed"));
    }

    [Test]
    public async Task Render_SourceRow_KeepsTheExternalIdTheOtherViewsDropAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        // The Timeline shows the record's name; the external id (where distinct) is one hover away in the
        // chip's tooltip rather than appended to the visible text.
        Assert.That(cut.Markup, Does.Contain("Liam Allen"));
        Assert.That(cut.Markup, Does.Not.Contain("Liam Allen (S8-287551)"));
    }

    /// <summary>
    /// The record chip names the type and the record alone: "person: Liam Allen", with the
    /// external id moved into the tooltip rather than appended to the visible name. The type prefix is a
    /// separate, dimmed span for display only ("person:"); the visible text of the two spans together
    /// must still read identically to the name alone.
    /// </summary>
    [Test]
    public async Task Render_SourceRowRecordChip_NamesTheTypeAndNameWithTheExternalIdInTheTooltipAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        var recordChip = cut.FindComponents<ObjectChip>().First(c => c.Instance.Kind == ObjectChipKind.ConnectedSystemObject);
        var chip = recordChip.Find(".jim-object-chip");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recordChip.FindComponent<MudTooltip>().Instance.Text,
                Is.EqualTo("person: Liam Allen · S8-287551 · in Yellowstone APAC"));
            Assert.That(chip.QuerySelector(".jim-object-chip-glyph")!.TextContent.Trim(), Is.EqualTo("CSO"));
            Assert.That(
                string.Concat(chip.QuerySelector(".jim-object-chip-type")!.TextContent, chip.QuerySelector(".jim-object-chip-name")!.TextContent).Trim(),
                Is.EqualTo("person: Liam Allen"));
            Assert.That(chip.QuerySelector(".jim-object-chip-type")!.TextContent.Trim(), Is.EqualTo("person:"),
                "the dimmed type prefix is its own span, split from the name purely for display");
        }
    }

    /// <summary>
    /// A target object reached through an outcome (here a queued export against an existing
    /// Connected System Object) is named "type: name" exactly as the source row's own chip is, once the
    /// "csId|csoTypeName" channel carries a type.
    /// </summary>
    [Test]
    public async Task Render_TargetObjectChip_NamesTheTypeAndNameAsALabelAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var projected = CausalityTestData.AddOutcome(item,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected, parent: null, ordinal: 0,
            targetEntityId: Guid.NewGuid(), targetEntityDescription: "Liam Allen");
        var export = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            parent: projected, ordinal: 0, targetEntityId: Guid.NewGuid(),
            targetEntityDescription: "Contoso AD", detailCount: 1, detailMessage: "3|user");
        var targetCsoId = Guid.NewGuid();
        export.ConnectedSystemObjectChange = new ConnectedSystemObjectChange { ConnectedSystemObjectId = targetCsoId };
        var pageContext = CausalityTestData.NewJoinerContext() with
        {
            ConnectedSystemObjectNames = new Dictionary<Guid, string> { [targetCsoId] = "EMP001746" }
        };
        var model = CausalityModelBuilder.Build(item, pageContext);

        var cut = RenderTimeline(context, model);

        var targetChip = cut.FindComponents<ObjectChip>().Single(c => c.Instance.Name == "EMP001746");
        var chip = targetChip.Find(".jim-object-chip");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.QuerySelector(".jim-object-chip-type")!.TextContent.Trim(), Is.EqualTo("user:"));
            Assert.That(targetChip.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo("user: EMP001746"));
        }
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
            "Connected System Object processed", "Projected to the Metaverse", "Attributes flowed", "Provisioned", "Export queued"
        }));
    }

    [Test]
    public async Task Render_RowWithChildren_RendersThemBesideItsBodyAndMarksTheRowAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        // A row's children are its own grid child, beside the body rather than inside it, so the rail's
        // cell spans only the parent's own content and its line meets the children's connector exactly
        // where the nested block begins. has-children on the row drives both that layout and the
        // body's dropped bottom padding (the last child already carries the trailing gap).
        Assert.That(cut.FindAll(".tl-body .tl-children"), Is.Empty, "children must not render inside a body");
        foreach (var row in cut.FindAll(".tl-row"))
        {
            var hasChildren = row.QuerySelector(":scope > .tl-children") != null;
            Assert.That(row.ClassList.Contains("has-children"), Is.EqualTo(hasChildren),
                $"'{row.QuerySelector(".tl-line .verb")?.TextContent.Trim()}' marks has-children as " +
                $"{!hasChildren} while it {(hasChildren ? "does" : "does not")} render a child container.");
        }
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
    public async Task Render_ProjectedRow_ShowsNoCsoOrMvoVocabularyAtAllAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        var projectedRow = cut.FindAll(".tl-row")[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(projectedRow.QuerySelector(".verb")!.TextContent.Trim(), Is.EqualTo("Projected to the Metaverse"));
            // The abbreviations live only in the entity chips' glyphs, where the full name sits in
            // the glyph's title; the timeline's own words never use them.
            var prose = string.Join(" ", cut.FindAll(".verb, .tl-detail-line, .evt-badge, .jim-object-chip-name, .jim-object-chip-type")
                .Select(e => e.TextContent));
            Assert.That(prose, Does.Not.Contain("MVO"));
            Assert.That(prose, Does.Not.Contain("CSO"));
        }
    }

    [Test]
    public async Task Render_LeaverScenario_MvoDeletedRowLinksToTheDeletionRecordBrowserAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        var deletionLinks = cut.FindAll("a[href='/admin/deleted-objects?t=deleted-mvos&mvo=11111111-1111-1111-1111-111111111111']");
        Assert.That(deletionLinks, Is.Not.Empty);
    }

    /// <summary>
    /// The Timeline's counterpart of CausalityEventCardTests' footer test: a queued deprovision's expander
    /// names what its rows are, because they identify the target rather than change it.
    /// </summary>
    [Test]
    public async Task Render_LeaverScenario_DeprovisionExpanderNamesItsRowsRatherThanCountingThemAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        var expanders = cut.FindAll(".tl-expander").Select(e => e.TextContent.Trim()).ToList();
        Assert.That(expanders, Has.Count.EqualTo(1),
            "Only the Glitterband EMEA deprovision carries a snapshot in this fixture");
        Assert.That(expanders[0], Does.Contain("Target identified by"));
        Assert.That(expanders[0], Does.Not.Contain("attribute"));
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
        Assert.That(AttributeRowCount(cut.FindAll(".tl-inline-detail tbody tr")), Is.EqualTo(3));
        Assert.That(cut.Find(".tl-expander").ClassList, Does.Contain("open"));

        cut.Find(".tl-expander").Click();

        Assert.That(cut.FindAll(".tl-inline-detail"), Is.Empty);
    }

    /// <summary>
    /// The operation chip (#1495 follow-up) is a Lineage-only affordance: <see cref="CausalityEventCard"/>
    /// only renders it when a caller passes its <c>Operation</c> parameter, and the Timeline does not use
    /// that shared card at all (it builds its own row markup). This is pinned even though the underlying
    /// model's events genuinely carry a populated <see cref="CausalityEvent.Operation"/> (the new joiner
    /// scenario's Projected, AttributeFlow and Provisioned events all do), so the guard is real rather
    /// than trivially true from an empty model.
    /// </summary>
    [Test]
    public async Task Render_AnyScenario_NeverRendersTheOperationChipAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        Assert.That(model.AllEvents().Any(e => e.Operation != null), Is.True,
            "the scenario must actually carry a populated Operation for this guard to mean anything");

        var cut = RenderTimeline(context, model);

        Assert.That(cut.FindAll(".ln-op"), Is.Empty);
    }

    /// <summary>
    /// The Lineage-only title suppression (#1495 second follow-up, <see cref="OutcomeDisplayMap.IsTitleSubsumedByOperation"/>)
    /// never reaches the Timeline: it builds its own row markup rather than passing HideTitle through
    /// <see cref="CausalityEventCard"/> at all, so Projected and Provisioned (both title-subsumed on the
    /// Lineage) keep printing their verb here exactly as before.
    /// </summary>
    [Test]
    public async Task Render_ProjectedAndProvisioned_StillPrintTheirVerbsAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        var verbs = cut.FindAll(".tl-line .verb").Select(v => v.TextContent.Trim()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verbs, Does.Contain("Projected to the Metaverse"));
            Assert.That(verbs, Does.Contain("Provisioned"));
        }
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
