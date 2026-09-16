// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using Microsoft.AspNetCore.Components.Web;
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
            // The downstream entry reads the provisioned Connected System Object's own identity, not the
            // Connected System's name (#1519 Table view fix 1); the system name moves to the subtitle.
            Assert.That(navNames, Has.Some.EqualTo($"person: {CausalityTestData.ProvisionedCsoId}"));
            var navSubs = cut.FindAll(".tv-nav-sub").Select(n => n.TextContent.Trim()).ToList();
            Assert.That(navSubs, Has.Some.EqualTo("Glitterband EMEA"));

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
            Assert.That(rows[0].TextContent, Does.Contain("Projection"));
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
        Assert.That(rows[0].TextContent, Does.Contain("Projection"));
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

    // ─── Truncated names get a tooltip (#1519 Table view fix 5) ───

    [Test]
    public void Render_NavButtonName_CarriesTheFullDisplayNameAsATitle()
    {
        var cut = Render(NewJoinerTableModel());

        var everythingName = cut.FindAll(".tv-nav-name").First();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(everythingName.TextContent.Trim(), Is.EqualTo("Everything"));
            Assert.That(everythingName.GetAttribute("title"), Is.EqualTo("Everything"));
        }
    }

    [Test]
    public void Render_EverythingObjectColumnCell_CarriesTheFullDisplayNameAsATitle()
    {
        var cut = Render(NewJoinerTableModel());

        var provisionRow = cut.FindAll("tbody tr").Single(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Provision");
        var objectCell = provisionRow.Children[0];
        var link = objectCell.QuerySelector("a")!;

        Assert.That(link.GetAttribute("title"), Is.EqualTo($"person: {CausalityTestData.ProvisionedCsoId}"));
    }

    // ─── Resizable sidebar (#1519 Table view fix 6) ───

    [Test]
    public void Render_ResizeHandle_IsAFocusableVerticalSeparatorWithALabel()
    {
        var cut = Render(NewJoinerTableModel());

        var handle = cut.Find(".tv-resize-handle");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handle.GetAttribute("role"), Is.EqualTo("separator"));
            Assert.That(handle.GetAttribute("aria-orientation"), Is.EqualTo("vertical"));
            Assert.That(handle.GetAttribute("aria-label"), Is.Not.Null.And.Not.Empty);
            Assert.That(handle.GetAttribute("tabindex"), Is.EqualTo("0"));
        }
    }

    // ─── Every object column value links (#1519 Table view fix 7) ───

    /// <summary>
    /// Only Downstream objects were linked before this fix; the Source and Identity are as addressable
    /// as any downstream object once the builder knows their href, and the Everything column must reflect
    /// that. Built directly, rather than through the model builder, so the test proves the view's own
    /// rendering rather than re-proving the builder tests already covering where each href comes from.
    /// </summary>
    [Test]
    public void Render_SourceAndIdentityObjectColumnCells_RenderAnchorsWhenHrefsAreKnown()
    {
        var model = new CausalityTableModel
        {
            Objects =
            [
                new CausalityTableObject("everything", CausalityTableObjectRole.Everything, "Everything", null, CausalityTone.Secondary, 2),
                new CausalityTableObject("source", CausalityTableObjectRole.Source, "Liam Allen", null, CausalityTone.Secondary, 1,
                    "/admin/connected-systems/1/connector-space/22222222-2222-2222-2222-222222222222"),
                new CausalityTableObject("identity", CausalityTableObjectRole.Identity, "Liam Allen", "Person", CausalityTone.Primary, 1,
                    "/t/people/v/11111111-1111-1111-1111-111111111111")
            ],
            Rows =
            [
                new CausalityTableRow("source", CausalityTableChangeKind.AttributeChange, "mail", "old", "new", null, null, "l", "t", CausalityTone.Info),
                new CausalityTableRow("identity", CausalityTableChangeKind.Join, null, null, null, null, null, "Joined to Identity", "CSO Joined", CausalityTone.Secondary)
            ],
            CurrentHeading = "Before",
            NextHeading = "After"
        };

        var cut = Render(model);

        var rows = cut.FindAll("tbody tr");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows[0].Children[0].QuerySelector("a"), Is.Not.Null, "the Source cell links when its href is known");
            Assert.That(rows[1].Children[0].QuerySelector("a"), Is.Not.Null, "the Identity cell links when its href is known");
        }
    }

    // ─── Before/After are for attribute values only (#1519 Table view fix 8) ───

    [Test]
    public void Render_ObjectLevelRow_LeavesCurrentAndWouldBeEmpty()
    {
        var cut = Render(NewJoinerTableModel());

        var provisionRow = cut.FindAll("tbody tr").Single(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Provision");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provisionRow.Children[3].TextContent.Trim(), Is.EqualTo("-"), "Before is for attribute values only");
            Assert.That(provisionRow.Children[4].TextContent.Trim(), Is.EqualTo("-"), "After is for attribute values only");
        }
    }

    [Test]
    public void Render_MvoDeletionScheduledRow_ShowsTheGraceReasoningAsAMutedOutcomeDetailLine()
    {
        const string graceReasoning = "Deletion Rule: last connector disconnected. Grace period: 7 days.";
        var model = new CausalityTableModel
        {
            Objects =
            [
                new CausalityTableObject("everything", CausalityTableObjectRole.Everything, "Everything", null, CausalityTone.Warning, 1),
                new CausalityTableObject("identity", CausalityTableObjectRole.Identity, "Liam Allen", "Person", CausalityTone.Warning, 1)
            ],
            Rows =
            [
                new CausalityTableRow("identity", CausalityTableChangeKind.Delete, null, null, null, null, null,
                    "Identity deletion scheduled", "MVO Deletion Scheduled", CausalityTone.Warning, graceReasoning)
            ],
            CurrentHeading = "Before",
            NextHeading = "After"
        };

        var cut = Render(model);

        var outcomeCell = cut.Find("tbody tr").Children.Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomeCell.TextContent, Does.Contain("Identity deletion scheduled"));
            var detail = outcomeCell.QuerySelector(".tv-outcome-detail");
            Assert.That(detail, Is.Not.Null);
            Assert.That(detail!.TextContent.Trim(), Is.EqualTo(graceReasoning));
        }
    }

    // ─── Deterministic order with a way back (#1519 Table view fix 9) ───

    [Test]
    public void Render_ClickingAHeaderThreeTimes_RestoresTheOriginalRowOrder()
    {
        var cut = Render(NewJoinerTableModel());
        var originalOrder = cut.FindAll("tbody tr").Select(r => r.TextContent).ToList();

        // Re-queried before each click: a click re-renders, and a handler id captured from the previous
        // render tree is stale.
        cut.FindAll(".tv-sort").Single(h => h.TextContent.Trim() == "Change").Click(); // ascending
        cut.FindAll(".tv-sort").Single(h => h.TextContent.Trim() == "Change").Click(); // descending
        var sortedOrder = cut.FindAll("tbody tr").Select(r => r.TextContent).ToList();
        cut.FindAll(".tv-sort").Single(h => h.TextContent.Trim() == "Change").Click(); // off: back to the model's own causal order

        var restoredOrder = cut.FindAll("tbody tr").Select(r => r.TextContent).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sortedOrder, Is.Not.EqualTo(originalOrder), "the column must actually have changed the order to prove anything");
            Assert.That(restoredOrder, Is.EqualTo(originalOrder));
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

    // ─── Attribute column empty for object-level rows (#1519 Table view fix 2) ───

    [Test]
    public void Render_ObjectLevelRow_LeavesTheAttributeCellEmpty()
    {
        var cut = Render(NewJoinerTableModel());

        var provisionRow = cut.FindAll("tbody tr").Single(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Provision");
        var attributeCell = provisionRow.Children[2];

        Assert.That(attributeCell.TextContent.Trim(), Is.EqualTo("-"), "an object-level row's Attribute cell renders the empty-value hyphen, not a connector label");
    }

    [Test]
    public void Render_AttributeChangeRow_StillNamesItsAttribute()
    {
        var cut = Render(NewJoinerTableModel());

        var attributeRow = cut.FindAll("tbody tr").Single(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Attribute change"
            && r.TextContent.Contains("mail"));
        var attributeCell = attributeRow.Children[2];

        Assert.That(attributeCell.TextContent.Trim(), Is.EqualTo("mail"));
    }

    // ─── Object column identity link (#1519 Table view fix 1) ───

    [Test]
    public void Render_EverythingObjectColumn_LinksTheObjectWhenItsIdentityIsKnown()
    {
        var cut = Render(NewJoinerTableModel());

        var provisionRow = cut.FindAll("tbody tr").Single(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Provision");
        var objectCell = provisionRow.Children[0];
        var link = objectCell.QuerySelector("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link, Is.Not.Null);
            Assert.That(link!.TextContent.Trim(), Is.EqualTo($"person: {CausalityTestData.ProvisionedCsoId}"));
            Assert.That(link.GetAttribute("href"), Does.Contain("/connector-space/"));
        }
    }

    [Test]
    public void Render_EverythingObjectColumn_PlainTextWhenTheObjectsIdentityIsUnknown()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var cut = Render(CausalityTableModelBuilder.Build(model));

        var deprovisionRows = cut.FindAll("tbody tr").Where(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Deprovision").ToList();

        Assert.That(deprovisionRows, Has.Count.GreaterThan(0));
        Assert.That(deprovisionRows, Has.All.Matches<AngleSharp.Dom.IElement>(r => r.Children[0].QuerySelector("a") == null));
    }

    // ─── Synchronisation Rule column (#1519 Table view fix 3) ───

    [Test]
    public void Render_TableHeaders_ShowSynchronisationRuleNotVia()
    {
        var cut = Render(NewJoinerTableModel());

        var headers = cut.FindAll("thead th").Select(h => h.TextContent.Trim()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers, Has.Some.EqualTo("Synchronisation Rule"));
            Assert.That(headers, Has.None.EqualTo("Via"));
        }
    }

    [Test]
    public void Render_RowWithKnownSyncRule_LinksTheSynchronisationRuleCell()
    {
        var cut = Render(NewJoinerTableModel());

        var provisionRow = cut.FindAll("tbody tr").Single(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Provision");
        var syncRuleCell = provisionRow.Children[5];
        var link = syncRuleCell.QuerySelector("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link, Is.Not.Null);
            Assert.That(link!.GetAttribute("href"), Is.EqualTo("/admin/sync-rules/9"));
            Assert.That(link.TextContent.Trim(), Is.EqualTo("Glitterband People - Outbound"));
        }
    }

    [Test]
    public void Render_RowWithReasoningTextOnly_RendersPlainTextNotALink()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var cut = Render(CausalityTableModelBuilder.Build(model));

        var deleteRow = cut.FindAll("tbody tr").Single(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Delete");
        var syncRuleCell = deleteRow.Children[5];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(syncRuleCell.QuerySelector("a"), Is.Null);
            Assert.That(syncRuleCell.TextContent.Trim(), Is.EqualTo("Deleted immediately: last authoritative source disconnected"));
        }
    }

    // ─── Resizable columns (#1519 Table view fix 10) ───

    private static IElement SelectIdentityObject(IRenderedComponent<CausalityTableView> cut)
    {
        var identityButton = cut.FindAll(".tv-nav-btn").Single(b =>
            b.QuerySelector(".tv-nav-name")!.TextContent.Trim() == "Liam Allen"
            && b.QuerySelector(".tv-nav-sub")?.TextContent.Trim() == "Person");
        identityButton.Click();
        return identityButton;
    }

    [Test]
    public void Render_EverythingSelected_EveryHeaderCellHasExactlyOneNamedColumnGrip()
    {
        var cut = Render(NewJoinerTableModel());

        var headers = cut.FindAll("thead th");
        var expectedLabels = new[]
        {
            "Resize the Object column", "Resize the Change column", "Resize the Attribute column",
            "Resize the Before column", "Resize the After column",
            "Resize the Synchronisation Rule column", "Resize the Outcome column"
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers, Has.Count.EqualTo(7));
            for (var i = 0; i < headers.Count; i++)
            {
                var grips = headers[i].QuerySelectorAll(".tv-col-grip");
                Assert.That(grips, Has.Count.EqualTo(1), $"header {i} ({headers[i].TextContent.Trim()}) must carry exactly one grip");
                var grip = grips[0];
                Assert.That(grip.GetAttribute("role"), Is.EqualTo("separator"));
                Assert.That(grip.GetAttribute("aria-orientation"), Is.EqualTo("vertical"));
                Assert.That(grip.GetAttribute("tabindex"), Is.EqualTo("0"));
                Assert.That(grip.GetAttribute("aria-label"), Is.EqualTo(expectedLabels[i]));
            }
        }
    }

    [Test]
    public void Render_SingleObjectSelected_SixHeaderCellsAndNoObjectGrip()
    {
        var cut = Render(NewJoinerTableModel());
        SelectIdentityObject(cut);

        var headers = cut.FindAll("thead th");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers, Has.Count.EqualTo(6));
            Assert.That(headers.SelectMany(h => h.QuerySelectorAll(".tv-col-grip"))
                .Select(g => g.GetAttribute("aria-label")), Has.None.EqualTo("Resize the Object column"));
        }
    }

    [Test]
    public void Render_EverythingSelected_ColgroupColCountMatchesHeaderCount()
    {
        var cut = Render(NewJoinerTableModel());

        Assert.That(cut.FindAll("colgroup col"), Has.Count.EqualTo(cut.FindAll("thead th").Count));
    }

    [Test]
    public void Render_SingleObjectSelected_ColgroupColCountMatchesHeaderCount()
    {
        var cut = Render(NewJoinerTableModel());
        SelectIdentityObject(cut);

        Assert.That(cut.FindAll("colgroup col"), Has.Count.EqualTo(cut.FindAll("thead th").Count));
    }

    [Test]
    public void Render_ArrowRightOnAColumnGrip_InvokesColumnResizerNudge()
    {
        var cut = Render(NewJoinerTableModel());

        var attributeGrip = cut.FindAll("thead th").Single(h => h.TextContent.Trim() == "Attribute")
            .QuerySelector(".tv-col-grip")!;
        attributeGrip.KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

        Assert.That(_context.JSInterop.Invocations.Any(i => i.Identifier == "jimTableViewColumnResizer.nudge"), Is.True);
    }

    [Test]
    public void Render_ArrowUpOnAColumnGrip_DoesNotInvokeColumnResizerNudge()
    {
        var cut = Render(NewJoinerTableModel());

        var attributeGrip = cut.FindAll("thead th").Single(h => h.TextContent.Trim() == "Attribute")
            .QuerySelector(".tv-col-grip")!;
        attributeGrip.KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });

        Assert.That(_context.JSInterop.Invocations.Any(i => i.Identifier == "jimTableViewColumnResizer.nudge"), Is.False);
    }

    [Test]
    public void Render_AttributeChangeRow_BeforeAfterAndAttributeCellsCarryTitles()
    {
        var model = new CausalityTableModel
        {
            Objects =
            [
                new CausalityTableObject("everything", CausalityTableObjectRole.Everything, "Everything", null, CausalityTone.Info, 1)
            ],
            Rows =
            [
                new CausalityTableRow("everything", CausalityTableChangeKind.AttributeChange, "mail",
                    "liam.allen@old.example.com", "liam.allen@example.com", null, null,
                    "Attribute change", "Attribute change", CausalityTone.Info)
            ],
            CurrentHeading = "Before",
            NextHeading = "After"
        };

        var cut = Render(model);

        var row = cut.Find("tbody tr");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Children[2].GetAttribute("title"), Is.EqualTo("mail"));
            Assert.That(row.Children[3].GetAttribute("title"), Is.EqualTo("liam.allen@old.example.com"));
            Assert.That(row.Children[4].GetAttribute("title"), Is.EqualTo("liam.allen@example.com"));
        }
    }

    [Test]
    public void Render_ObjectLevelRow_EmptyBeforeCellCarriesNoTitle()
    {
        var cut = Render(NewJoinerTableModel());

        var provisionRow = cut.FindAll("tbody tr").Single(r => r.QuerySelector(".tv-kind")!.TextContent.Trim() == "Provision");
        Assert.That(provisionRow.Children[3].HasAttribute("title"), Is.False);
    }

    [Test]
    public void Render_OutcomeCellWithoutDetail_CarriesTitleEqualToTheLabelAlone()
    {
        var model = new CausalityTableModel
        {
            Objects = [new CausalityTableObject("everything", CausalityTableObjectRole.Everything, "Everything", null, CausalityTone.Success, 1)],
            Rows =
            [
                new CausalityTableRow("everything", CausalityTableChangeKind.Provision, null, null, null, null, null,
                    "Object provisioned", "CSO Provisioned", CausalityTone.Success)
            ],
            CurrentHeading = "Before",
            NextHeading = "After"
        };

        var cut = Render(model);

        var outcomeCell = cut.Find("tbody tr").Children.Last();
        Assert.That(outcomeCell.GetAttribute("title"), Is.EqualTo("Object provisioned"));
    }

    [Test]
    public void Render_OutcomeCellWithDetail_CarriesTitleJoiningLabelAndDetail()
    {
        const string graceReasoning = "Deletion Rule: last connector disconnected. Grace period: 7 days.";
        var model = new CausalityTableModel
        {
            Objects = [new CausalityTableObject("everything", CausalityTableObjectRole.Everything, "Everything", null, CausalityTone.Warning, 1)],
            Rows =
            [
                new CausalityTableRow("everything", CausalityTableChangeKind.Delete, null, null, null, null, null,
                    "Identity deletion scheduled", "MVO Deletion Scheduled", CausalityTone.Warning, graceReasoning)
            ],
            CurrentHeading = "Before",
            NextHeading = "After"
        };

        var cut = Render(model);

        var outcomeCell = cut.Find("tbody tr").Children.Last();
        Assert.That(outcomeCell.GetAttribute("title"), Is.EqualTo($"Identity deletion scheduled: {graceReasoning}"));
    }

    [Test]
    public void Render_FirstRender_InvokesColumnResizerAttach()
    {
        var cut = Render(NewJoinerTableModel());

        cut.WaitForAssertion(() =>
            Assert.That(_context.JSInterop.Invocations.Any(i => i.Identifier == "jimTableViewColumnResizer.attach"), Is.True));
    }
}
