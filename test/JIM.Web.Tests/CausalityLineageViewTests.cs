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

    /// <summary>
    /// A sync item whose projecting item lies further back than the page's own root: the Identity column
    /// would otherwise stay empty even though the fact is known (#1495 follow-up).
    /// </summary>
    private static CausalityLineageModel IdentityCreationLineage()
    {
        var item = CausalityTestData.NewJoinerItem();
        var chain = CausalityTestData.Chain(item.Id, truncatedByDepth: false,
            CausalityTestData.Cohort(
                default,
                metaverseChangeType: ObjectChangeType.Projected,
                connectedSystemId: 1,
                members: CausalityTestData.Member("Liam Allen", Guid.NewGuid(),
                    CausalChainResolution.Resolved,
                    occurred: CausalityTestData.ChainBaseTime)));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext(), chain: chain);
        return CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Projected);
    }

    /// <summary>
    /// The confirmation story: an import confirming an earlier export, which states no object operation.
    /// </summary>
    private static CausalityLineageModel ConfirmingImportLineage()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ImportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ImportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.ExportCausedImportConfirmation,
                connectedSystemId: 1, connectedSystemName: "Yellowstone APAC",
                members: CausalityTestData.Member("Liam Allen", ExportItemId,
                    CausalChainResolution.NoFurtherCauses)));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext(), chain: chain);
        return CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.PendingExportConfirmed);
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

    /// <summary>
    /// The head states what the object is, never what has since become of it. A head carries no time of its
    /// own, so a state marker sitting beside the name is read as something this run did; the run that created
    /// an Identity must not appear to have struck it out.
    /// </summary>
    [Test]
    public void Render_ObjectDeletedAfterThisRun_LeavesTheHeadStatingOnlyWhatTheObjectIs()
    {
        var cut = RenderLineage(DeletedAfterThisRunModel());

        var head = cut.Find(".ln-obj");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(head.QuerySelector(".ln-obj-title")!.ClassList, Does.Not.Contain("gone"));
            Assert.That(head.QuerySelector(".evt-badge"), Is.Null);
            Assert.That(head.QuerySelector(".ln-link"), Is.Null);
        }
    }

    /// <summary>
    /// The fact sits below the run's own events, where the panel already reads top-to-bottom as time
    /// passing, and says outright that it came afterwards. Position and wording agree, so neither has to
    /// carry the tense alone.
    /// </summary>
    [Test]
    public void Render_ObjectDeletedAfterThisRun_SaysSoBeneathTheRunsEvents()
    {
        var cut = RenderLineage(DeletedAfterThisRunModel());

        var body = cut.Find(".ln-obj-body");
        var since = body.QuerySelector(".ln-since")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(since.TextContent, Does.Contain("was deleted after this run"));
            Assert.That(since.QuerySelector("a.ln-link")!.GetAttribute("href"),
                Is.EqualTo("/admin/deleted-objects?t=deleted-mvos&mvo=abc"));
            Assert.That(since.QuerySelector("a.ln-link")!.TextContent.Trim(), Is.EqualTo("View deletion record"));
            Assert.That(body.LastElementChild, Is.SameAs(since),
                "the deletion happened after everything else on this object, so it renders after it");
        }
    }

    /// <summary>
    /// An object with no events of its own still gets its body, because the note is the only thing it has
    /// to say. Guarding the body on the cards alone would silently drop it.
    /// </summary>
    [Test]
    public void Render_ObjectDeletedAfterThisRunWithNoEvents_StillSaysSo()
    {
        var model = new CausalityLineageModel
        {
            Columns =
            [
                new CausalityLineageColumn
                {
                    Kind = CausalityLineageColumnKind.Identity,
                    Objects =
                    [
                        new CausalityLineageObject
                        {
                            Title = "Test Deprov JoinDisc",
                            DeletedAfterThisRunHref = "/admin/deleted-objects?t=deleted-mvos&mvo=abc"
                        }
                    ]
                }
            ],
            Joins = []
        };

        var cut = RenderLineage(model);

        Assert.That(cut.Find(".ln-since").TextContent, Does.Contain("was deleted after this run"));
    }

    /// <summary>
    /// An object this run deleted says nothing about a later deletion: its own card recorded it, and the
    /// note exists purely for what happened outside this item's story.
    /// </summary>
    [Test]
    public void Render_ObjectDeletedByThisRun_AddsNoAfterThisRunNote()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Disconnected);

        var cut = RenderLineage(lineage);

        Assert.That(cut.FindAll(".ln-since"), Is.Empty);
    }

    /// <summary>
    /// An Identity created by this run and deleted afterwards, which is the shape that made the shipped
    /// treatment read as a contradiction.
    /// </summary>
    private static CausalityLineageModel DeletedAfterThisRunModel()
    {
        var model = CausalityModelBuilder.Build(
            CausalityTestData.NewJoinerItem(),
            CausalityTestData.NewJoinerContext());
        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);
        var identity = lineage.Columns.Single(c => c.Kind == CausalityLineageColumnKind.Identity).Objects.Single();

        return new CausalityLineageModel
        {
            Columns =
            [
                new CausalityLineageColumn
                {
                    Kind = CausalityLineageColumnKind.Identity,
                    Objects =
                    [
                        new CausalityLineageObject
                        {
                            Title = identity.Title,
                            ObjectTypeName = identity.ObjectTypeName,
                            Cards = identity.Cards,
                            DeletedAfterThisRunHref = "/admin/deleted-objects?t=deleted-mvos&mvo=abc"
                        }
                    ]
                }
            ],
            Joins = []
        };
    }

    /// <summary>
    /// An object JIM cannot build a route to explains itself, and says only that: it may well still exist,
    /// so the wording never claims otherwise. A role head ("Records") is exempt, because it stands for
    /// several objects and was never going to link to one.
    /// </summary>
    [Test]
    public void Render_UnaddressableObject_ExplainsItselfWithoutClaimingItIsGone()
    {
        var model = new CausalityLineageModel
        {
            Columns =
            [
                new CausalityLineageColumn
                {
                    Kind = CausalityLineageColumnKind.Identity,
                    Objects = [new CausalityLineageObject { Title = "Test Deprov JoinDisc" }]
                },
                new CausalityLineageColumn
                {
                    Kind = CausalityLineageColumnKind.Record,
                    Objects = [new CausalityLineageObject { Title = "Records", IsRoleHead = true }]
                }
            ],
            Joins = [new CausalityLineageJoin(null)]
        };

        var cut = RenderLineage(model);

        var titles = cut.FindAll(".ln-obj-title");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(titles[0].GetAttribute("title"), Is.EqualTo("JIM cannot open a page for this object."));
            Assert.That(titles[1].GetAttribute("title"), Is.Null, "a role head stands for several objects");
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

    /// <summary>
    /// An object's body reads top to bottom as time passing: its cards are ordered oldest first, and the
    /// note about what became of the object afterwards is stated last because it happened last. An ending
    /// says what lies behind the *oldest* card ("No earlier causes recorded"), so it belongs at the top of
    /// that order, not the bottom.
    /// <para>
    /// Rendered after the cards, it read as a flat contradiction wherever the story fitted in one column: a
    /// confirming import showed the export that caused it, then this run's events, then "No earlier causes
    /// recorded" beneath an earlier cause plainly visible above it (#1528).
    /// </para>
    /// </summary>
    [Test]
    public void Render_ChainEnding_SitsAboveTheCardsItIsAboutAsync()
    {
        var cut = RenderLineage(ExportCreateLineage());

        // The source record's object: the one carrying both an ending and a card.
        var body = cut.FindAll(".ln-obj-body")
            .First(b => b.QuerySelector(".ln-end") != null);
        var children = body.Children.ToList();
        var endingIndex = children.FindIndex(c => c.ClassList.Contains("ln-end"));
        var firstCardIndex = children.FindIndex(c =>
            c.ClassList.Contains("ln-now") || c.ClassList.Contains("ln-card"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(endingIndex, Is.GreaterThanOrEqualTo(0), "the ending must render at all");
            Assert.That(firstCardIndex, Is.GreaterThanOrEqualTo(0), "the fixture's object must carry a card to order against");
            Assert.That(endingIndex, Is.LessThan(firstCardIndex),
                "an ending describes what lies behind the oldest card, and the column runs oldest to newest " +
                "downwards, so rendering it last puts it at the wrong end of the story");
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

    /// <summary>
    /// Every chain-hop card carries its tone-tinted operation chip (#1495 follow-up), with the tone
    /// exposed as inline custom properties on the card exactly as a this-run event card exposes them
    /// (mirrors <c>CausalityEventCard.razor</c>).
    /// </summary>
    [Test]
    public void Render_ChainCards_EachCarryTheirOperationChipWithToneVariables()
    {
        var cut = RenderLineage(ExportCreateLineage());

        var chainCards = cut.FindAll(".ln-card");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainCards, Has.Count.EqualTo(2));
            foreach (var card in chainCards)
            {
                var chip = card.QuerySelector(".ln-op");
                Assert.That(chip, Is.Not.Null);
                Assert.That(chip!.TextContent.Trim(), Is.EqualTo("Created"));
                var style = card.GetAttribute("style")!;
                Assert.That(style, Does.Contain("--tone:"));
                Assert.That(style, Does.Contain("--tone-text:"));
            }
        }
    }

    /// <summary>
    /// The technical-names toggle swaps the chip's label exactly as it swaps an event card's, so the
    /// vocabulary a reader asked for is consistent across every card on the panel.
    /// </summary>
    [Test]
    public void Render_TechnicalNames_SwapsTheOperationChipLabel()
    {
        var cut = RenderLineage(IdentityCreationLineage(), technicalNames: true);

        var chainCard = cut.Find(".ln-card");
        Assert.That(chainCard.QuerySelector(".ln-op")!.TextContent.Trim(), Is.EqualTo("MVO Projected"));
    }

    /// <summary>
    /// A confirmation is not an object operation, so it carries no chip and no tone custom properties.
    /// </summary>
    [Test]
    public void Render_ConfirmationChainCard_CarriesNoOperationChip()
    {
        var cut = RenderLineage(ConfirmingImportLineage());

        var chainCard = cut.Find(".ln-card");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainCard.TextContent, Does.Contain("exported, and this import confirms it"));
            Assert.That(chainCard.QuerySelector(".ln-op"), Is.Null);
            Assert.That(chainCard.GetAttribute("style"), Is.Null);
        }
    }

    /// <summary>
    /// The Identity-creation card (#1495 follow-up) renders as a chain card under the Identity column,
    /// stating the Identity's own creation even though the projecting item lies further back than this
    /// page's own root.
    /// </summary>
    [Test]
    public void Render_IdentityCreationCard_RendersUnderTheIdentityColumn()
    {
        var cut = RenderLineage(IdentityCreationLineage());

        var identityObject = cut.FindAll(".ln-object")
            .Single(o => o.QuerySelector(".glyph")!.TextContent.Trim() == "ID");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identityObject.QuerySelector(".ln-card"), Is.Not.Null,
                "the creation hop is a chain card under the Identity column");
            Assert.That(identityObject.QuerySelector(".ln-card")!.TextContent,
                Does.Contain("was created as a new Identity"));
        }
    }

    /// <summary>
    /// A this-run event card carries the same operation chip a chain card does (#1495 follow-up):
    /// the Lineage view is the one caller that passes <c>Operation</c> through to
    /// <see cref="CausalityEventCard"/>, so a column scan finds an operation marker on every card,
    /// this run's included, not just on earlier runs' chain cards.
    /// </summary>
    [Test]
    public void Render_ThisRunEventCard_CarriesTheEventsOwnOperationChip()
    {
        var cut = RenderLineage(ExportCreateLineage());

        var thisRunCard = cut.Find(".ln-now .evt-card");
        var chip = thisRunCard.QuerySelector(".ln-op");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip, Is.Not.Null);
            Assert.That(chip!.TextContent.Trim(), Is.EqualTo("Created"));
            // First child of the card, exactly as a chain card's own chip leads it.
            Assert.That(thisRunCard.Children.First().ClassList, Does.Contain("ln-op"));
        }
    }

    // ─── Redundant card titles suppressed on the Lineage (#1495 second follow-up) ───

    /// <summary>
    /// Projected and Provisioned both carry a Lineage join label (PROJECTED / PROVISIONED) stating the
    /// same verb their own operation chip already states, so their card heads are redundant and must
    /// not render on this view: the chip becomes the card's only stated name for the outcome.
    /// </summary>
    [Test]
    public void Render_ProjectedAndProvisionedThisRunCards_ShowTheChipButSuppressTheHead()
    {
        var cut = RenderLineage(NewJoinerLineage());

        var cards = cut.FindComponents<CausalityEventCard>();
        var projected = cards.Single(c => c.Instance.Event.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        var provisioned = cards.Single(c => c.Instance.Event.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(projected.Find(".evt-card").QuerySelector(".ln-op"), Is.Not.Null,
                "the chip must still render");
            Assert.That(projected.FindAll(".evt-head"), Is.Empty, "the head is the restated fact");
            Assert.That(provisioned.Find(".evt-card").QuerySelector(".ln-op"), Is.Not.Null);
            Assert.That(provisioned.FindAll(".evt-head"), Is.Empty);
        }
    }

    /// <summary>
    /// Exported is deliberately excluded from title suppression: its decision-specific titles ("Record
    /// created" here) are not restated by any Lineage join label, so its card head must keep rendering
    /// even though the card also carries an operation chip.
    /// </summary>
    [Test]
    public void Render_ExportedThisRunCard_KeepsItsTitle()
    {
        var cut = RenderLineage(ExportCreateLineage());

        var exportedCard = cut.FindComponents<CausalityEventCard>()
            .Single(c => c.Instance.Event.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportedCard.Find(".evt-card").QuerySelector(".ln-op"), Is.Not.Null,
                "the fixture must actually carry a chip for this guard to mean anything");
            Assert.That(exportedCard.Find(".evt-title").TextContent.Trim(), Is.EqualTo("Record created"));
        }
    }
}
