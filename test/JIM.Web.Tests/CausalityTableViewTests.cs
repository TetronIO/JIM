// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityTableView"/> (#1519 Phase 3): the left navigation of objects
/// touched, the Everything object's leading Object column, per-object row filtering, the change-kind
/// filter chips, and the speculative Current/Would be headings.
/// </summary>
[TestFixture]
public class CausalityTableViewTests
{
    private BunitContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _context = CausalityBunitContext.Create();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _context.DisposeAsync();
    }

    private static CausalityTableModel NewJoinerTableModel()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        return CausalityTableModelBuilder.Build(model);
    }

    private IRenderedComponent<CausalityTableView> Render(CausalityTableModel model, bool technicalNames = false)
    {
        return _context.Render<CausalityTableView>(ps => ps
            .Add(c => c.Model, model)
            .Add(c => c.TechnicalNames, technicalNames));
    }

    [Test]
    public void Render_NewJoinerModel_ShowsNavGroupsAndEverythingsRows()
    {
        var cut = Render(NewJoinerTableModel());

        using (Assert.EnterMultipleScope())
        {
            var captions = cut.FindAll(".tv-group-caption").Select(c => c.TextContent.Trim()).ToList();
            Assert.That(captions, Is.EqualTo(new[] { "Object being synchronised", "Identity", "Downstream objects" }));

            var navNames = cut.FindAll(".tv-nav-name").Select(n => n.TextContent.Trim()).ToList();
            Assert.That(navNames[0], Is.EqualTo("Everything"), "Everything is first, outside every group");
            Assert.That(navNames, Has.Some.EqualTo("Glitterband EMEA"));

            // Everything is selected by default, so the grid carries the leading Object column.
            Assert.That(cut.Find("thead tr").Children.First().TextContent.Trim(), Is.EqualTo("Object"));
            Assert.That(cut.FindAll("tbody tr"), Has.Count.EqualTo(6),
                "1 join/projection + 1 provision + 1 export queued + 3 attribute rows");
        }
    }

    [Test]
    public void Render_SelectingAnObject_FiltersRowsToItAndDropsTheObjectColumn()
    {
        var cut = Render(NewJoinerTableModel());

        var identityButton = cut.FindAll(".tv-nav-btn").Single(b => b.TextContent.Contains("Identity") == false
            && b.QuerySelector(".tv-nav-name")!.TextContent.Trim() == "Liam Allen"
            && b.QuerySelector(".tv-nav-sub")?.TextContent.Trim() == "Person");
        identityButton.Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("thead tr").Children.First().TextContent.Trim(), Is.Not.EqualTo("Object"),
                "a single-object selection carries no Object column");
            var rows = cut.FindAll("tbody tr");
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].TextContent, Does.Contain("Join / Projection"));
        }
    }

    [Test]
    public void Render_DestructiveFilter_ShowsOnlyDestructiveRows()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var cut = Render(CausalityTableModelBuilder.Build(model));

        cut.FindAll(".filter-chips button").Single(b => b.TextContent.Trim() == "Destructive").Click();

        var rows = cut.FindAll("tbody tr");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.GreaterThan(0));
            Assert.That(rows.Select(r => r.TextContent),
                Has.All.Matches<string>(t => t.Contains("Delete") || t.Contains("Deprovision")));
        }
    }

    [Test]
    public void Render_AttributeChangesFilter_ShowsOnlyAttributeRows()
    {
        var cut = Render(NewJoinerTableModel());

        cut.FindAll(".filter-chips button").Single(b => b.TextContent.Trim() == "Attribute changes").Click();

        var rows = cut.FindAll("tbody tr");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(rows.Select(r => r.TextContent), Has.All.Matches<string>(t => t.Contains("Attribute change")));
        }
    }

    [Test]
    public void Render_ScopeAndJoinFilter_ExcludesEverythingElse()
    {
        var cut = Render(NewJoinerTableModel());

        cut.FindAll(".filter-chips button").Single(b => b.TextContent.Trim() == "Scope & Join").Click();

        var rows = cut.FindAll("tbody tr");
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].TextContent, Does.Contain("Join / Projection"));
    }

    [Test]
    public void Render_TechnicalNames_SwapsTheOutcomeColumnToTechnicalLabels()
    {
        var cutPlain = Render(NewJoinerTableModel());
        var cutTechnical = Render(NewJoinerTableModel(), technicalNames: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cutPlain.Markup, Does.Contain("Identity created"));
            Assert.That(cutTechnical.Markup, Does.Contain("MVO Projected"));
        }
    }

    [Test]
    public void Render_SpeculativeModel_UsesCurrentWouldBeHeadings()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree = [new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected }]
        };
        var model = CausalityModelBuilder.BuildSpeculative(preview, CausalityTestData.NewJoinerContext());
        var cut = Render(CausalityTableModelBuilder.Build(model));

        var headers = cut.FindAll("thead th").Select(h => h.TextContent.Trim()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers, Has.Some.EqualTo("Current"));
            Assert.That(headers, Has.Some.EqualTo("Would be"));
            Assert.That(headers, Has.None.EqualTo("Before"));
        }
    }

    [Test]
    public void Render_RecordedModel_UsesBeforeAfterHeadings()
    {
        var cut = Render(NewJoinerTableModel());

        var headers = cut.FindAll("thead th").Select(h => h.TextContent.Trim()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers, Has.Some.EqualTo("Before"));
            Assert.That(headers, Has.Some.EqualTo("After"));
        }
    }

    [Test]
    public void Render_NavButtons_AreKeyboardOperableWithAriaPressed()
    {
        var cut = Render(NewJoinerTableModel());

        var buttons = cut.FindAll(".tv-nav-btn");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buttons, Has.All.Matches<AngleSharp.Dom.IElement>(b => b.TagName == "BUTTON"));
            Assert.That(buttons[0].GetAttribute("aria-pressed"), Is.EqualTo("true"), "Everything starts selected");
        }
    }

    [Test]
    public void Render_RemovedAttributeValue_StrikesThroughTheCurrentCellWithNoWouldBe()
    {
        var preview = new SyncPreviewResult
        {
            Inbound = new SyncPreviewInboundSummary
            {
                AttributeFlowChanges = [new SyncPreviewAttributeFlowChange { AttributeName = "mail", IsAddition = false, Value = "liam.allen@example.com" }]
            },
            OutcomeTree = [new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, DetailCount = 1 }]
        };
        var model = CausalityModelBuilder.BuildSpeculative(preview, CausalityTestData.NewJoinerContext());
        var cut = Render(CausalityTableModelBuilder.Build(model));

        var removedCell = cut.FindAll("td.tv-removed");
        Assert.That(removedCell, Has.Count.EqualTo(1));
        Assert.That(removedCell[0].TextContent, Does.Contain("liam.allen@example.com"));
    }
}
