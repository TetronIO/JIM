// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityLineageView"/> (#1495): column heads carrying the R/ID chip
/// vocabulary with the system named beneath, this-run cards rendered primary (ring and badge) around
/// the shared event card, chain cards rendered subdued with their run kind, timestamp and activity
/// link, cohort expansion in place, endings as quiet footers, the technical-names cascade and the
/// selection callback for the shared attribute drawer.
/// </summary>
[TestFixture]
public class CausalityLineageViewTests
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
    private static CausalityLineageModel ExportCreateLineage(bool truncated = false)
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
        return CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);
    }

    /// <summary>
    /// The cohort story: a group's update export caused by ten deleted Users.
    /// </summary>
    private static CausalityLineageModel CohortLineage()
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
        return CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);
    }

    /// <summary>
    /// The sync new-joiner story, whose staged export card carries attribute rows and is therefore
    /// clickable for the drawer.
    /// </summary>
    private static CausalityLineageModel NewJoinerLineage()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        return CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);
    }

    private IRenderedComponent<CausalityLineageView> RenderLineage(
        CausalityLineageModel model, bool technicalNames = false,
        Action<CausalityEvent?>? onSelectionChanged = null,
        DateTime? timestamp = null)
    {
        return _context.Render<CausalityLineageView>(ps =>
        {
            ps.Add(c => c.Model, model);
            ps.Add(c => c.TechnicalNames, technicalNames);
            ps.Add(c => c.Timestamp, timestamp);
            if (onSelectionChanged != null)
                ps.Add(c => c.SelectedEventChanged, onSelectionChanged);
        });
    }

    /// <summary>
    /// The leaver story deprovisions two systems, so its target side holds two records in one column.
    /// Each has to enclose its own head and events: proximity alone would leave a reader unable to say
    /// where one record's story ended, and in this story the two records even share a title, so the
    /// enclosure and the system sub-line are the only things telling them apart.
    /// </summary>
    [Test]
    public void Render_ColumnHoldingSeveralRecords_EnclosesEachWithItsOwnHeadAndEvents()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Disconnected);

        var cut = RenderLineage(lineage);

        var targetColumn = cut.FindAll(".ln-col")[2];
        var objects = targetColumn.QuerySelectorAll(":scope > .ln-object");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects, Has.Length.EqualTo(2));
            foreach (var enclosure in objects)
            {
                Assert.That(enclosure.QuerySelectorAll(":scope > .ln-obj"), Has.Length.EqualTo(1),
                    "one head per enclosure: the head is what the enclosure belongs to");
                Assert.That(enclosure.QuerySelectorAll(":scope > .ln-obj-body > .ln-now"), Has.Length.EqualTo(1),
                    "each record's own events live inside its own enclosure, not loose in the column");
            }

            Assert.That(objects[0].TextContent, Does.Contain("Glitterband EMEA"));
            Assert.That(objects[1].TextContent, Does.Contain("Contoso AD"));
        }
    }

    /// <summary>
    /// The canvas is bounded by its sides, so its grid never grows a track per Connected System. This is
    /// asserted on the inline template because that is where the width actually comes from.
    /// </summary>
    [Test]
    public void Render_ManyTargetSystems_KeepsTheCanvasToItsColumnTracks()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var projected = CausalityTestData.AddOutcome(item,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected, parent: null, ordinal: 0,
            targetEntityId: Guid.NewGuid(), targetEntityDescription: "Liam Allen");
        for (var systemId = 2; systemId <= 9; systemId++)
        {
            CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                parent: projected, ordinal: systemId, targetEntityId: Guid.NewGuid(),
                targetEntityDescription: $"System {systemId}", detailMessage: $"{systemId}|person");
        }
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        var cut = RenderLineage(lineage);

        var template = cut.Find(".ln-canvas").GetAttribute("style")!;
        Assert.That(template.Split("minmax").Length - 1, Is.LessThanOrEqualTo(4),
            $"eight target systems must not become eight column tracks: {template}");
    }

    /// <summary>
    /// The Connected System an object lives in is a link to that system, exactly as its name is
    /// everywhere else on the panel. The head is where a reader meets the system, and it was the one
    /// mention of it with nowhere to go.
    /// </summary>
    [Test]
    public void Render_RecordHead_LinksItsConnectedSystem()
    {
        var cut = RenderLineage(ExportCreateLineage());

        var sub = cut.FindAll(".ln-obj-sub")[0];
        var link = sub.QuerySelector("a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link, Is.Not.Null);
            Assert.That(link!.GetAttribute("href"), Is.EqualTo("/admin/connected-systems/1"));
            Assert.That(link.TextContent.Trim(), Is.EqualTo("Yellowstone APAC"),
                "only the system's name is the link, not the whole 'record in ...' phrase");
            Assert.That(sub.TextContent.Trim(), Is.EqualTo("record in Yellowstone APAC"));
        }
    }

    /// <summary>
    /// A system whose id the snapshots did not carry still names itself; it simply does not link.
    /// </summary>
    [Test]
    public void Render_RecordHeadWithNoSystemId_NamesTheSystemWithoutLinkingIt()
    {
        var model = new CausalityLineageModel
        {
            Columns =
            [
                new CausalityLineageColumn
                {
                    Kind = CausalityLineageColumnKind.Record,
                    Objects = [new CausalityLineageObject { Title = "Liam Allen", SystemName = "Retired System" }]
                }
            ],
            Joins = []
        };

        var cut = RenderLineage(model);

        var sub = cut.Find(".ln-obj-sub");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sub.QuerySelector("a"), Is.Null);
            Assert.That(sub.TextContent.Trim(), Is.EqualTo("record in Retired System"));
        }
    }

    [Test]
    public void Render_ExportCreateStory_HeadsColumnsWithRecordAndIdentityChips()
    {
        var cut = RenderLineage(ExportCreateLineage());

        var heads = cut.FindAll(".ln-obj");
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
        var cut = RenderLineage(ExportCreateLineage());

        var thisRun = cut.Find(".ln-now");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thisRun.QuerySelector(".ln-now-badge")!.TextContent.Trim(), Is.EqualTo("This run"));
            Assert.That(thisRun.QuerySelector(".evt-card"), Is.Not.Null,
                "this-run cards reuse the shared event card so tones, links and the drawer keep working");
            Assert.That(cut.FindComponents<CausalityEventCard>(), Has.Count.EqualTo(1));
        }
    }

    /// <summary>
    /// This run's cards say when they happened too. Leaving them bare while every chain card carried a time
    /// made the panel look as though it recorded a time for some events and not others, when in truth the run's
    /// time was simply somewhere else (the summary band, at the top left of a left-to-right story).
    /// </summary>
    [Test]
    public void Render_ThisRunCard_CarriesTheRunsTimeBesideItsBadge()
    {
        var executed = new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);

        var cut = RenderLineage(ExportCreateLineage(), timestamp: executed);

        var time = cut.Find(".ln-now .ln-time");
        AssertRelativeTimeWithFullDateTooltip(time, executed);
    }

    /// <summary>
    /// A run this panel does not know the time of renders no time at all, rather than the epoch.
    /// </summary>
    [Test]
    public void Render_ThisRunCard_WithNoTimestamp_RendersNoTime()
    {
        var cut = RenderLineage(ExportCreateLineage());

        Assert.That(cut.FindAll(".ln-now .ln-time"), Is.Empty);
    }

    [Test]
    public void Render_ChainCard_IsSubduedWithRunKindTimestampAndActivityLink()
    {
        var cut = RenderLineage(ExportCreateLineage());

        var chainCards = cut.FindAll(".ln-card");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainCards, Has.Count.EqualTo(2));
            var importCard = chainCards.Single(c => c.TextContent.Contains("Import run"));
            Assert.That(importCard.QuerySelector($"a[href='/activity/item/{ImportItemId}']"), Is.Not.Null);
            Assert.That(importCard.QuerySelector(".ln-time"), Is.Not.Null, "the card carries its timestamp");
            var syncCard = chainCards.Single(c => c.TextContent.Contains("Synchronisation run"));
            Assert.That(syncCard.TextContent, Does.Contain("provisioned"));
        }
    }

    /// <summary>
    /// One treatment for every time on the panel: relative in the text, the full value on hover, matching the
    /// summary band above. The chain cards used to print the absolute date instead, so the same kind of fact
    /// was written two ways on one screen.
    /// </summary>
    [Test]
    public void Render_ChainCard_ShowsRelativeTimeWithTheFullDateInItsTooltip()
    {
        var cut = RenderLineage(ExportCreateLineage());

        var importCard = cut.FindAll(".ln-card").Single(c => c.TextContent.Contains("Import run"));
        AssertRelativeTimeWithFullDateTooltip(importCard.QuerySelector(".ln-time")!, CausalityTestData.ChainBaseTime);
    }

    private static void AssertRelativeTimeWithFullDateTooltip(IElement time, DateTime utc)
    {
        var local = utc.ToLocalTime();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(time.TextContent.Trim(), Is.EqualTo(local.ToRelativeTime()));
            Assert.That(time.GetAttribute("title"), Is.EqualTo(local.ToFriendlyDate()));
        }
    }

    [Test]
    public void Render_PluralCohort_CollapsesToOneCardAndExpandsInPlace()
    {
        var cut = RenderLineage(CohortLineage());

        var toggle = cut.Find(".ln-members-toggle");
        Assert.That(toggle.TextContent.Trim(), Is.EqualTo("Show the 10 Users"));
        Assert.That(cut.FindAll(".ln-member"), Is.Empty);

        toggle.Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".ln-member"), Has.Count.EqualTo(10));
            Assert.That(cut.Find(".ln-members-toggle").TextContent.Trim(), Is.EqualTo("Hide the 10 Users"));
            Assert.That(cut.FindAll(".ln-member").Select(m => m.TextContent), Has.Some.Contains("User 3"));
        }
    }

    [Test]
    public void Render_ChainEnding_RendersAsAQuietColumnFooter()
    {
        var cut = RenderLineage(ExportCreateLineage());

        var endings = cut.FindAll(".ln-end");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(endings, Has.Count.EqualTo(1));
            Assert.That(endings[0].TextContent.Trim(), Is.EqualTo("No earlier causes recorded"));
        }
    }

    [Test]
    public void Render_TruncatedChain_SaysSomeBranchesGoFurtherBack()
    {
        var cut = RenderLineage(ExportCreateLineage(truncated: true));

        Assert.That(cut.Find(".ln-truncated").TextContent,
            Does.Contain("Some branches go further back than shown"));
    }

    [Test]
    public void Render_TechnicalNames_FlowThroughToTheEventCards()
    {
        var cut = RenderLineage(NewJoinerLineage(), technicalNames: true);

        Assert.That(cut.FindComponents<CausalityEventCard>(),
            Has.All.Matches<IRenderedComponent<CausalityEventCard>>(card => card.Instance.TechnicalNames));
    }

    [Test]
    public void Render_ClickingAClickableEventCard_RaisesSelectionChanged()
    {
        CausalityEvent? selected = null;
        var cut = RenderLineage(NewJoinerLineage(), onSelectionChanged: e => selected = e);

        cut.Find(".evt-card.clickable").Click();

        Assert.That(selected, Is.Not.Null);
        Assert.That(selected!.OutcomeType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
    }

    [Test]
    public void Render_JoinLabels_RenderBetweenColumns()
    {
        var cut = RenderLineage(ExportCreateLineage());

        Assert.That(cut.FindAll(".ln-join-label").Select(l => l.TextContent.Trim()),
            Is.EqualTo(new[] { "imported", "provisioned" }));
    }

    [Test]
    public void Render_JoinWithNoRelationship_IsAnEmptyGutterNotABareArrow()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                (CausalEdgeType)99,
                members: CausalityTestData.Member("Mystery cause", Guid.NewGuid(),
                    CausalChainResolution.NoFurtherCauses)));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);
        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var cut = RenderLineage(lineage);

        // The record column and the trailing unassigned column are adjacent but unrelated: an arrow
        // between them would claim a relationship the model does not state.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".ln-join"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(".ln-join-arrow"), Is.Empty);
            Assert.That(cut.FindAll(".ln-join-label"), Is.Empty);
        }
    }
}
