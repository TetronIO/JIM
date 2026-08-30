// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
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
    /// <summary>
    /// How many attribute rows a rendered detail table is actually showing. The rows live in a virtualised grid,
    /// which brackets them with two empty spacer rows (that is how a virtualiser reserves the height of what it
    /// has not rendered), so counting every row in the body counts two that carry nothing.
    /// </summary>
    private static int AttributeRowCount(IReadOnlyList<IElement> rows) =>
        rows.Count(row => row.Children.Length > 0);

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
    public async Task Render_TechnicalNames_RenamesTheOpeningVerbTooAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var plain = RenderTimeline(context, model);
        Assert.That(plain.FindAll(".verb")[0].TextContent.Trim(), Is.EqualTo("Record processed"));

        await using var technicalContext = CausalityBunitContext.Create();
        var technical = RenderTimeline(technicalContext, model, technicalNames: true);
        Assert.That(technical.FindAll(".verb")[0].TextContent.Trim(),
            Is.EqualTo("Connected System Object processed"));
    }

    [Test]
    public async Task Render_SourceRow_KeepsTheExternalIdTheOtherViewsDropAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        // The Timeline is the one view with room to be precise: it reads RecordLabel while the summary
        // sentence and the Flow and Graph views read RecordName. Pinned so the two never quietly converge.
        Assert.That(cut.Markup, Does.Contain("Liam Allen (S8-287551)"));
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
    public async Task Render_RowWithChildren_MarksItsBodySoTheTrailingGapIsNotCountedTwiceAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model);

        // Children render inside their parent's body, so a parent that keeps its own bottom padding
        // adds it on top of the last child's: the gap after a nested branch came out double the gap
        // between siblings, and compounded once more per level.
        foreach (var row in cut.FindAll(".tl-row"))
        {
            var body = row.QuerySelector(".tl-body")!;
            var hasChildren = body.QuerySelector(":scope > .tl-children") != null;
            Assert.That(body.ClassList.Contains("has-children"), Is.EqualTo(hasChildren),
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
    public async Task Render_PlainNames_ShowsNoTechnicalVocabularyAtAllAsync()
    {
        // Same rule as the Lineage view's cards: with the toggle off, no CSO or MVO vocabulary appears.
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model, technicalNames: false);

        var projectedRow = cut.FindAll(".tl-row")[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(projectedRow.QuerySelector(".verb")!.TextContent.Trim(), Is.EqualTo("Identity created"));
            Assert.That(projectedRow.QuerySelector(".tech"), Is.Null);
            Assert.That(cut.Markup, Does.Not.Contain("MVO Projected"));
        }
    }

    [Test]
    public async Task Render_TechnicalNames_ShowsTheTechnicalLabelInsteadAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var cut = RenderTimeline(context, model, technicalNames: true);

        var projectedRow = cut.FindAll(".tl-row")[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(projectedRow.QuerySelector(".verb")!.TextContent.Trim(), Is.EqualTo("MVO Projected"));
            Assert.That(projectedRow.QuerySelector(".tech"), Is.Null);
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
            Assert.That(verbs, Does.Contain("Identity created"));
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
