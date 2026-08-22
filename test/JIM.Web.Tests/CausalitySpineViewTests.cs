// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalitySpineView"/> (#1495): column heads carrying the R/ID chip
/// vocabulary with the system named beneath, this-run cards rendered primary (ring and badge) around
/// the shared event card, chain cards rendered subdued with their run kind, timestamp and activity
/// link, cohort expansion in place, endings as quiet footers, the technical-names cascade and the
/// selection callback for the shared attribute drawer.
/// </summary>
[TestFixture]
public class CausalitySpineViewTests
{
    private static readonly Guid SyncItemId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ImportItemId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ExportItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");

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

    /// <summary>
    /// The create-export story: source record (chain import hop and an ending), Identity (empty,
    /// completing the graph), target record (queueing hop plus this run's export).
    /// </summary>
    private static CausalitySpineModel ExportCreateSpine(bool truncated = false)
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 11);
        var chain = CausalityTestData.Chain(ExportItemId, truncated,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportCreateStaged,
                connectedSystemId: 2, connectedSystemName: "Glitterband EMEA",
                syncRuleId: 9, syncRuleName: "Glitterband People - Outbound",
                members: CausalityTestData.Member("Liam Allen", SyncItemId,
                    occurred: CausalityTestData.ChainBaseTime.AddMinutes(10),
                    causes: CausalityTestData.Cohort(
                        default,
                        sourceImportChangeType: ObjectChangeType.Added,
                        connectedSystemId: 1, connectedSystemName: "Yellowstone APAC",
                        members: CausalityTestData.Member("Liam Allen", ImportItemId,
                            CausalChainResolution.NoFurtherCauses,
                            occurred: CausalityTestData.ChainBaseTime)))));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);
        return CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);
    }

    /// <summary>
    /// The cohort story: a group's update export caused by ten deleted Users.
    /// </summary>
    private static CausalitySpineModel CohortSpine()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var deletedUsers = Enumerable.Range(1, 10)
            .Select(i => CausalityTestData.Member($"User {i}", Guid.NewGuid(),
                CausalChainResolution.CauseNotRetained,
                occurred: CausalityTestData.ChainBaseTime.AddMinutes(i)))
            .ToArray();
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportUpdateStaged,
                connectedSystemId: 2, connectedSystemName: "Glitterband EMEA",
                members: CausalityTestData.Member("Project Diamond", SyncItemId,
                    occurred: CausalityTestData.ChainBaseTime.AddMinutes(30),
                    causes: CausalityTestData.Cohort(
                        CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                        objectTypeName: "User", objectTypePluralName: "Users",
                        attributeName: "Static Members",
                        members: deletedUsers))));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);
        return CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);
    }

    /// <summary>
    /// The sync new-joiner story, whose staged export card carries attribute rows and is therefore
    /// clickable for the drawer.
    /// </summary>
    private static CausalitySpineModel NewJoinerSpine()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        return CausalitySpineModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);
    }

    private IRenderedComponent<CausalitySpineView> RenderSpine(
        CausalitySpineModel model, bool technicalNames = false,
        Action<CausalityEvent?>? onSelectionChanged = null)
    {
        return _context.Render<CausalitySpineView>(ps =>
        {
            ps.Add(c => c.Model, model);
            ps.Add(c => c.TechnicalNames, technicalNames);
            if (onSelectionChanged != null)
                ps.Add(c => c.SelectedEventChanged, onSelectionChanged);
        });
    }

    [Test]
    public void Render_ExportCreateStory_HeadsColumnsWithRecordAndIdentityChips()
    {
        var cut = RenderSpine(ExportCreateSpine());

        var heads = cut.FindAll(".sp-obj");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(heads, Has.Count.EqualTo(3));
            Assert.That(heads[0].QuerySelector(".glyph")!.TextContent.Trim(), Is.EqualTo("R"));
            Assert.That(heads[0].TextContent, Does.Contain("Liam Allen"));
            Assert.That(heads[0].TextContent, Does.Contain("record in Yellowstone APAC"));
            Assert.That(heads[1].QuerySelector(".glyph")!.TextContent.Trim(), Is.EqualTo("ID"));
            Assert.That(heads[2].TextContent, Does.Contain("record in Glitterband EMEA"));
            // The column is the record, never the system: no head carries the CS glyph.
            Assert.That(heads.Select(h => h.QuerySelector(".glyph")!.TextContent.Trim()),
                Has.None.EqualTo("CS"));
        }
    }

    [Test]
    public void Render_ThisRunCard_IsPrimaryWithBadgeAroundTheSharedEventCard()
    {
        var cut = RenderSpine(ExportCreateSpine());

        var thisRun = cut.Find(".sp-now");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thisRun.QuerySelector(".sp-now-badge")!.TextContent.Trim(), Is.EqualTo("This run"));
            Assert.That(thisRun.QuerySelector(".evt-card"), Is.Not.Null,
                "this-run cards reuse the shared event card so tones, links and the drawer keep working");
            Assert.That(cut.FindComponents<CausalityEventCard>(), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Render_ChainCard_IsSubduedWithRunKindTimestampAndActivityLink()
    {
        var cut = RenderSpine(ExportCreateSpine());

        var chainCards = cut.FindAll(".sp-card");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainCards, Has.Count.EqualTo(2));
            var importCard = chainCards.Single(c => c.TextContent.Contains("Import run"));
            Assert.That(importCard.QuerySelector($"a[href='/activity/item/{ImportItemId}']"), Is.Not.Null);
            Assert.That(importCard.TextContent, Does.Contain("2026"), "the card carries its timestamp");
            var syncCard = chainCards.Single(c => c.TextContent.Contains("Synchronisation run"));
            Assert.That(syncCard.TextContent, Does.Contain("provisioned"));
        }
    }

    [Test]
    public void Render_PluralCohort_CollapsesToOneCardAndExpandsInPlace()
    {
        var cut = RenderSpine(CohortSpine());

        var toggle = cut.Find(".sp-members-toggle");
        Assert.That(toggle.TextContent.Trim(), Is.EqualTo("Show the 10 Users"));
        Assert.That(cut.FindAll(".sp-member"), Is.Empty);

        toggle.Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".sp-member"), Has.Count.EqualTo(10));
            Assert.That(cut.Find(".sp-members-toggle").TextContent.Trim(), Is.EqualTo("Hide the 10 Users"));
            Assert.That(cut.FindAll(".sp-member").Select(m => m.TextContent), Has.Some.Contains("User 3"));
        }
    }

    [Test]
    public void Render_ChainEnding_RendersAsAQuietColumnFooter()
    {
        var cut = RenderSpine(ExportCreateSpine());

        var endings = cut.FindAll(".sp-end");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(endings, Has.Count.EqualTo(1));
            Assert.That(endings[0].TextContent.Trim(), Is.EqualTo("End of the recorded causality chain"));
        }
    }

    [Test]
    public void Render_TruncatedChain_SaysSomeBranchesGoFurtherBack()
    {
        var cut = RenderSpine(ExportCreateSpine(truncated: true));

        Assert.That(cut.Find(".sp-truncated").TextContent,
            Does.Contain("Some branches go further back than shown"));
    }

    [Test]
    public void Render_TechnicalNames_FlowThroughToTheEventCards()
    {
        var cut = RenderSpine(NewJoinerSpine(), technicalNames: true);

        Assert.That(cut.FindComponents<CausalityEventCard>(),
            Has.All.Matches<IRenderedComponent<CausalityEventCard>>(card => card.Instance.TechnicalNames));
    }

    [Test]
    public void Render_ClickingAClickableEventCard_RaisesSelectionChanged()
    {
        CausalityEvent? selected = null;
        var cut = RenderSpine(NewJoinerSpine(), onSelectionChanged: e => selected = e);

        cut.Find(".evt-card.clickable").Click();

        Assert.That(selected, Is.Not.Null);
        Assert.That(selected!.OutcomeType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
    }

    [Test]
    public void Render_JoinLabels_RenderBetweenColumns()
    {
        var cut = RenderSpine(ExportCreateSpine());

        Assert.That(cut.FindAll(".sp-join-label").Select(l => l.TextContent.Trim()),
            Is.EqualTo(new[] { "imported", "provisioned" }));
    }
}
