// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The upward causal walk (#1223): the read half of Causal Provenance, where the cohort model the PRD settled
/// on is actually applied.
///
/// The walk's whole job is to turn a flat table of edges into something an administrator can read. Three
/// things make that non-trivial and are what these tests pin: causes that say the same thing must collapse
/// into one counted statement rather than repeating; causes that say different things must fork rather than
/// flatten; and a chain that cannot be followed further must end in a named state rather than a gap, because
/// "nothing caused this" and "the cause is no longer retained" look identical as an absence and mean opposite
/// things to the reader.
/// </summary>
[TestFixture]
public class CausalChainWalkTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    private readonly Dictionary<Guid, List<CausalEdge>> _edgesByEffectItemId = new();
    private readonly HashSet<Guid> _retainedItemIds = [];
    private readonly Dictionary<Guid, CausalChainItemSummary> _summariesById = new();
    private readonly Dictionary<Guid, CausalSourceImportEvent> _importEventsByCsoId = new();
    private readonly Dictionary<(int SystemId, string ExternalId), CausalSourceImportEvent> _importEventsByExternalId = new();
    private readonly Dictionary<Guid, Guid> _exportItemIdsByPendingExportId = new();

    [SetUp]
    public void SetUp()
    {
        _edgesByEffectItemId.Clear();
        _retainedItemIds.Clear();
        _summariesById.Clear();
        _importEventsByCsoId.Clear();
        _importEventsByExternalId.Clear();
        _exportItemIdsByPendingExportId.Clear();

        _mockRepository = new Mock<IRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);

        _mockActivityRepository
            .Setup(r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) =>
                ids.SelectMany(id => _edgesByEffectItemId.TryGetValue(id, out var edges) ? edges : []).ToList());

        _mockActivityRepository
            .Setup(r => r.GetRunProfileExecutionItemCausalSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids
                .Where(_retainedItemIds.Contains)
                // A neutral change type by default: only tests that opt an item into a synchronisation-side
                // summary via _summariesById exercise the source-import hop.
                .ToDictionary(id => id, id => _summariesById.GetValueOrDefault(id)
                    ?? new CausalChainItemSummary { Id = id, ObjectChangeType = ObjectChangeType.PendingExport }));

        _mockActivityRepository
            .Setup(r => r.GetExportExecutionItemIdsByPendingExportIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids
                .Where(_exportItemIdsByPendingExportId.ContainsKey)
                .ToDictionary(id => id, id => _exportItemIdsByPendingExportId[id]));

        _mockActivityRepository
            .Setup(r => r.GetLatestImportItemForCsoAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<Guid>()))
            .ReturnsAsync((Guid csoId, DateTime _, Guid _) => _importEventsByCsoId.GetValueOrDefault(csoId));

        _mockActivityRepository
            .Setup(r => r.GetLatestImportItemForExternalIdAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<Guid>()))
            .ReturnsAsync((int systemId, string externalId, DateTime _, Guid _) =>
                _importEventsByExternalId.GetValueOrDefault((systemId, externalId)));

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    /// <summary>
    /// The ordinary case. Most items describe a change with a local explanation and have no edges at all, so
    /// the walk must return an empty chain rather than treating "no causes" as anything unusual.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ItemWithNoEdges_ReturnsAnEmptyChainAsync()
    {
        var itemId = Guid.NewGuid();

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.RunProfileExecutionItemId, Is.EqualTo(itemId));
        Assert.That(chain.Cohorts, Is.Empty);
        Assert.That(chain.IsTruncatedByDepth, Is.False);
    }

    /// <summary>
    /// The headline case: ten objects deleted for the same reason on the same Connected System, all
    /// contributing to one removal, must read as ONE statement of ten, not ten statements. That is the whole
    /// argument of the cohort model; without it the PRD's ten-leaver example produces ten near-identical hops
    /// an administrator has to read individually to discover they all say the same thing.
    /// </summary>
    [Test]
    public async Task GetCausalChain_TenCausesSharingAnAttributionTuple_CollapseToOneCohortAsync()
    {
        var itemId = Guid.NewGuid();
        var outcomeId = Guid.NewGuid();
        SeedEdges(itemId, Enumerable.Range(0, 10)
            .Select(i => NewEdge(itemId, effectOutcomeId: outcomeId, causeName: $"Member {i}"))
            .ToArray());

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts, Has.Count.EqualTo(1),
            "causes that say the same thing are one statement; repeating them ten times is what the cohort model exists to prevent");
        Assert.That(chain.Cohorts[0].MemberCount, Is.EqualTo(10));
        Assert.That(chain.Cohorts[0].Members.Select(m => m.DisplayName),
            Is.EquivalentTo(Enumerable.Range(0, 10).Select(i => $"Member {i}")),
            "the cohort must still name its members, so expanding it is useful rather than just a count");
        Assert.That(chain.Cohorts[0].ReasonCode, Is.EqualTo(CausalReasonCode.AllAuthoritativeSourcesDisconnected));
        Assert.That(chain.Cohorts[0].ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
    }

    /// <summary>
    /// Causes that differ in ANY element of the attribution tuple are different statements and must fork. Two
    /// root causes converging on one effect is the signal an administrator most needs; flattening them into
    /// one cohort would report a single cause for something that had two.
    /// </summary>
    [Test]
    public async Task GetCausalChain_CausesDifferingInReason_ForkIntoSeparateCohortsAsync()
    {
        var itemId = Guid.NewGuid();
        var outcomeId = Guid.NewGuid();
        SeedEdges(itemId,
            NewEdge(itemId, effectOutcomeId: outcomeId, causeName: "Scheduled leaver"),
            NewEdge(itemId, effectOutcomeId: outcomeId, causeName: "Last connector gone",
                reasonCode: CausalReasonCode.LastConnectorDisconnected));

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts, Has.Count.EqualTo(2),
            "two different reasons are two different statements, and collapsing them would hide one of the causes");
        Assert.That(chain.Cohorts.Select(c => c.ReasonCode), Is.EquivalentTo(new[]
        {
            CausalReasonCode.AllAuthoritativeSourcesDisconnected, CausalReasonCode.LastConnectorDisconnected
        }));
    }

    /// <summary>
    /// Grouping is per effect OUTCOME, not per item. This is the resolution of the PRD's edge-granularity
    /// question: an item carrying several outcomes has several independent stories, and merging their causes
    /// would attribute a removal on one outcome to a cause belonging to another.
    /// </summary>
    [Test]
    public async Task GetCausalChain_SameTupleOnDifferentOutcomes_StaysAsSeparateCohortsAsync()
    {
        var itemId = Guid.NewGuid();
        var firstOutcome = Guid.NewGuid();
        var secondOutcome = Guid.NewGuid();
        SeedEdges(itemId,
            NewEdge(itemId, effectOutcomeId: firstOutcome, causeName: "Alice"),
            NewEdge(itemId, effectOutcomeId: secondOutcome, causeName: "Bob"));

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts, Has.Count.EqualTo(2),
            "one item can carry several independent stories; merging them would attribute a cause to the wrong outcome");
        Assert.That(chain.Cohorts.Select(c => c.EffectSyncOutcomeId), Is.EquivalentTo(new Guid?[] { firstOutcome, secondOutcome }));
    }

    /// <summary>
    /// The walk continues through a cause whose own item is retained and itself has causes. This is what makes
    /// the feature more than a single hop: the PRD's example resolves back through two Connected Systems and
    /// two Activities.
    /// </summary>
    [Test]
    public async Task GetCausalChain_CauseWithItsOwnCauses_WalksUpwardAsync()
    {
        var itemId = Guid.NewGuid();
        var causeItemId = Guid.NewGuid();
        var grandCauseItemId = Guid.NewGuid();

        SeedEdges(itemId, NewEdge(itemId, causeItemId: causeItemId, causeName: "Group removal"));
        SeedEdges(causeItemId, NewEdge(causeItemId, causeItemId: grandCauseItemId, causeName: "Deleted identity"));
        Retain(causeItemId, grandCauseItemId);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var firstHop = chain.Cohorts.Single().Members.Single();
        Assert.That(firstHop.Resolution, Is.EqualTo(CausalChainResolution.Resolved));
        Assert.That(firstHop.DisplayName, Is.EqualTo("Group removal"));

        var secondHop = firstHop.Causes.Single().Members.Single();
        Assert.That(secondHop.DisplayName, Is.EqualTo("Deleted identity"));
        Assert.That(secondHop.Resolution, Is.EqualTo(CausalChainResolution.NoFurtherCauses),
            "the grandparent is retained but uncaused, which is a complete chain rather than a truncated one");
    }

    /// <summary>
    /// A cause whose item exists but has no causes of its own is a genuine root, and must be distinguishable
    /// from one whose item has been purged. Both look like "no more edges"; they mean opposite things.
    /// </summary>
    [Test]
    public async Task GetCausalChain_RetainedCauseWithNoCauses_ReportsNoFurtherCausesAsync()
    {
        var itemId = Guid.NewGuid();
        var causeItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: causeItemId, causeName: "Source attribute change"));
        Retain(causeItemId);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts.Single().Members.Single().Resolution,
            Is.EqualTo(CausalChainResolution.NoFurtherCauses));
    }

    /// <summary>
    /// A cause whose item has aged out of history must resolve to an explicit terminal state, still carrying
    /// the name snapshotted on the edge. This is the normal end of a long chain once a deployment has been
    /// live longer than one retention window, and it must never surface as a gap or an exception: an
    /// unexplained blank here is precisely the "this change has no cause whatsoever" defect the feature exists
    /// to remove.
    /// </summary>
    [Test]
    public async Task GetCausalChain_CauseNoLongerRetained_ReportsTheTerminalStateAndStillNamesItAsync()
    {
        var itemId = Guid.NewGuid();
        var purgedItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: purgedItemId, causeName: "Tina Adams (S8-99)"));
        // Deliberately not retained.

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var member = chain.Cohorts.Single().Members.Single();
        Assert.That(member.Resolution, Is.EqualTo(CausalChainResolution.CauseNotRetained));
        Assert.That(member.DisplayName, Is.EqualTo("Tina Adams (S8-99)"),
            "the edge's own snapshot is what lets a truncated chain still say what the lost cause was");
        Assert.That(member.Causes, Is.Empty);
    }

    /// <summary>
    /// A cause that named no item at all (the seam identified it by object rather than by event) is a terminal
    /// state too, not a purged one. Reporting it as not-retained would tell the reader something was lost when
    /// nothing was.
    /// </summary>
    [Test]
    public async Task GetCausalChain_CauseWithNoRecordedItem_ReportsNoFurtherCausesAsync()
    {
        var itemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: null, causeName: "Yellowstone APAC export"));

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts.Single().Members.Single().Resolution,
            Is.EqualTo(CausalChainResolution.NoFurtherCauses),
            "no recorded causing item is not the same as a purged one; only the latter lost information");
    }

    /// <summary>
    /// The walk is bounded, and hitting the bound must be reported rather than silently presented as the end
    /// of the story. A cascade can chain arbitrarily far, and an unbounded walk on a deep chain is both a
    /// performance risk and a way to lock the page.
    /// </summary>
    [Test]
    public async Task GetCausalChain_DeeperThanTheBound_StopsAndSaysSoAsync()
    {
        // A chain of five, walked with a bound of two.
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < 5; i++)
            SeedEdges(ids[i], NewEdge(ids[i], causeItemId: ids[i + 1], causeName: $"Level {i + 1}"));
        Retain(ids.ToArray());

        var chain = await _application.Activities.GetCausalChainAsync(ids[0], maxDepth: 2);

        Assert.That(chain.IsTruncatedByDepth, Is.True,
            "stopping at the bound must be distinguishable from reaching the end of the story");
        var first = chain.Cohorts.Single().Members.Single();
        var second = first.Causes.Single().Members.Single();
        Assert.That(second.Resolution, Is.EqualTo(CausalChainResolution.DepthLimitReached));
        Assert.That(second.Causes, Is.Empty);
    }

    /// <summary>
    /// The object type joins the grouping key, so a cohort's noun is always right for every member. Without
    /// it a cohort could mix a User and a Contractor, and no single noun would be correct for the statement
    /// the cohort renders.
    /// </summary>
    [Test]
    public async Task GetCausalChain_CausesOfDifferentObjectTypes_DoNotShareACohortAsync()
    {
        var itemId = Guid.NewGuid();
        var outcomeId = Guid.NewGuid();
        var user = NewEdge(itemId, effectOutcomeId: outcomeId, causeName: "Tina Adams");
        user.CauseObjectTypeName = "User";
        user.CauseObjectTypePluralName = "Users";
        var contractor = NewEdge(itemId, effectOutcomeId: outcomeId, causeName: "Sam Reed");
        contractor.CauseObjectTypeName = "Contractor";
        contractor.CauseObjectTypePluralName = "Contractors";
        SeedEdges(itemId, user, contractor);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts, Has.Count.EqualTo(2),
            "one noun cannot describe a mixed cohort, so the type has to separate them");
        Assert.That(chain.Cohorts.Select(c => c.ObjectNoun), Is.EquivalentTo(new[] { "User", "Contractor" }));
    }

    /// <summary>
    /// Two removals through different reference attributes are different statements: the chain names the
    /// attribute each removal happened on, so one cohort cannot speak for both.
    /// </summary>
    [Test]
    public async Task GetCausalChain_CausesOnDifferentAttributes_DoNotShareACohortAsync()
    {
        var itemId = Guid.NewGuid();
        var outcomeId = Guid.NewGuid();
        var members = NewEdge(itemId, effectOutcomeId: outcomeId, causeName: "Tina Adams");
        members.EffectAttributeName = "Static Members";
        var owners = NewEdge(itemId, effectOutcomeId: outcomeId, causeName: "Tina Adams");
        owners.EffectAttributeName = "Owners";
        SeedEdges(itemId, members, owners);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts, Has.Count.EqualTo(2));
        Assert.That(chain.Cohorts.Select(c => c.AttributeName), Is.EquivalentTo(new[] { "Static Members", "Owners" }));
    }

    /// <summary>
    /// The noun follows the count, and neither form is derived from the other. English pluralisation is
    /// unreliable on administrator-authored type names, so both are snapshotted and the cohort simply picks.
    /// </summary>
    [Test]
    public async Task GetCausalChain_CohortNoun_FollowsTheMemberCountAsync()
    {
        var singleItemId = Guid.NewGuid();
        var single = NewEdge(singleItemId, causeName: "Tina Adams");
        single.CauseObjectTypeName = "Person";
        single.CauseObjectTypePluralName = "People";
        SeedEdges(singleItemId, single);

        var manyItemId = Guid.NewGuid();
        SeedEdges(manyItemId, Enumerable.Range(0, 3).Select(i =>
        {
            var edge = NewEdge(manyItemId, causeName: $"Member {i}");
            edge.CauseObjectTypeName = "Person";
            edge.CauseObjectTypePluralName = "People";
            return edge;
        }).ToArray());

        var singleChain = await _application.Activities.GetCausalChainAsync(singleItemId);
        var manyChain = await _application.Activities.GetCausalChainAsync(manyItemId);

        Assert.That(singleChain.Cohorts.Single().ObjectNoun, Is.EqualTo("Person"));
        Assert.That(manyChain.Cohorts.Single().ObjectNoun, Is.EqualTo("People"),
            "the curated plural is used verbatim; a rule would have produced \"Persons\"");
    }

    /// <summary>
    /// A type with no curated plural falls back to the singular rather than to a guess or to nothing. Slightly
    /// stiff English beats a wrong word, and beats a sentence with a hole in it.
    /// </summary>
    [Test]
    public async Task GetCausalChain_NoCuratedPlural_FallsBackToTheSingularAsync()
    {
        var itemId = Guid.NewGuid();
        var edge = NewEdge(itemId, causeName: "Kit A");
        edge.CauseObjectTypeName = "Equipment";
        edge.CauseObjectTypePluralName = null;
        var second = NewEdge(itemId, causeName: "Kit B");
        second.CauseObjectTypeName = "Equipment";
        second.CauseObjectTypePluralName = null;
        SeedEdges(itemId, edge, second);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts.Single().ObjectNoun, Is.EqualTo("Equipment"));
    }

    /// <summary>
    /// One query per level, not one per cause. A cascade cohort can hold thousands of members, and a walk that
    /// queried per member would issue thousands of round trips to render one panel.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ManyCausesAcrossLevels_QueriesOncePerLevelAsync()
    {
        var itemId = Guid.NewGuid();
        var causeIds = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToList();
        SeedEdges(itemId, causeIds.Select(c => NewEdge(itemId, causeItemId: c, causeName: "m")).ToArray());
        Retain(causeIds.ToArray());

        await _application.Activities.GetCausalChainAsync(itemId, maxDepth: 3);

        _mockActivityRepository.Verify(
            r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()),
            Times.AtMost(3),
            "the walk must batch each level into one query; per-member queries would not survive a real cascade");
    }

    #region the source-import hop (#1223, complete export chain)

    private Guid SeedSyncCauseSummary(Guid itemId, ObjectChangeType changeType = ObjectChangeType.Projected)
    {
        var csoId = Guid.NewGuid();
        _retainedItemIds.Add(itemId);
        _summariesById[itemId] = new CausalChainItemSummary
        {
            Id = itemId,
            ObjectChangeType = changeType,
            ConnectedSystemObjectId = csoId,
            ActivityExecuted = new DateTime(2026, 8, 15, 11, 0, 0, DateTimeKind.Utc)
        };
        return csoId;
    }

    /// <summary>
    /// When the import in <see cref="SeedImportEvent"/> ran: before the synchronisation that consumed it, as a
    /// real timeline always is.
    /// </summary>
    private static readonly DateTime ImportExecuted = new(2026, 8, 15, 6, 30, 0, DateTimeKind.Utc);

    private Guid SeedImportEvent(Guid csoId, ObjectChangeType changeType = ObjectChangeType.Added)
    {
        var importItemId = Guid.NewGuid();
        _retainedItemIds.Add(importItemId);
        _importEventsByCsoId[csoId] = new CausalSourceImportEvent
        {
            RunProfileExecutionItemId = importItemId,
            ChangeType = changeType,
            DisplayName = "Mia Young (S8-352)",
            ConnectedSystemId = 1,
            ConnectedSystemName = "Yellowstone APAC",
            Occurred = ImportExecuted
        };
        return importItemId;
    }

    /// <summary>
    /// The hop that completes an export's chain: the synchronisation that staged the change resolves, and the
    /// record's own timeline continues behind it to the import that fed it, which then ends as the true root.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ResolvedSynchronisationCause_ContinuesToTheImportThatFedItAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        var csoId = SeedSyncCauseSummary(syncItemId);
        var importItemId = SeedImportEvent(csoId);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var syncMember = chain.Cohorts.Single().Members.Single();
        Assert.That(syncMember.Resolution, Is.EqualTo(CausalChainResolution.Resolved),
            "a synchronisation whose record has a retained import must resolve rather than end the chain");
        // SeedSyncCauseSummary defaults to Projected, so the resolved cause also carries the derived
        // Identity-creation cohort (#1495 follow-up) beside its source-import hop; that cohort has its own
        // dedicated tests, so this one isolates the hop it is actually about.
        Assert.That(syncMember.Causes, Has.Count.EqualTo(2));
        var sourceHop = syncMember.Causes.Single(c => c.SourceImportChangeType != null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceHop.SourceImportChangeType, Is.EqualTo(ObjectChangeType.Added));
            Assert.That(sourceHop.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(sourceHop.Members.Single().RunProfileExecutionItemId, Is.EqualTo(importItemId),
                "the hop must link to the import item so the walk and the reader can continue there");
            Assert.That(sourceHop.Members.Single().DisplayName, Is.EqualTo("Mia Young (S8-352)"));
            Assert.That(sourceHop.Members.Single().Resolution, Is.EqualTo(CausalChainResolution.NoFurtherCauses),
                "an import with no edges of its own is the true root: data arrived from the source system");
        }
    }

    /// <summary>
    /// The hop carries when its import ran. Every other cohort's members take their time from the stored edge,
    /// and this one is built by hand outside that path, so it silently arrived with no time at all: the Lineage
    /// suppresses a timestamp it does not have, which is why source-import cards were the only cards on the
    /// panel with no "when" (and why they sorted to the top of their column by default(DateTime) rather than by
    /// having happened first).
    /// </summary>
    [Test]
    public async Task GetCausalChain_SourceImportHop_CarriesWhenTheImportRanAsync()
    {
        var itemId = Guid.NewGuid();
        var csoId = SeedSyncCauseSummary(itemId);
        SeedImportEvent(csoId, ObjectChangeType.Updated);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts[0].Members.Single().Occurred, Is.EqualTo(ImportExecuted));
    }

    /// <summary>
    /// A synchronisation item viewed directly gets the same hop at the root, so the chain reads identically
    /// wherever the reader enters it.
    /// </summary>
    [Test]
    public async Task GetCausalChain_SynchronisationItemViewedDirectly_GetsTheSourceImportHopAtRootAsync()
    {
        var itemId = Guid.NewGuid();
        var csoId = SeedSyncCauseSummary(itemId);
        SeedImportEvent(csoId, ObjectChangeType.Updated);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts, Has.Count.EqualTo(1),
            "an item with no edges but a retained import behind its record has a chain, not an empty panel");
        Assert.That(chain.Cohorts[0].SourceImportChangeType, Is.EqualTo(ObjectChangeType.Updated));
    }

    /// <summary>
    /// The hop applies to synchronisation-side items only. An import item is itself the far end of the
    /// timeline, and a staging item's Connected System Object belongs to the effect, not the cause; giving
    /// either a timeline hop would invent a cause the walk has no grounds for.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ImportSideCause_GetsNoSourceImportHopAsync()
    {
        var itemId = Guid.NewGuid();
        var importCauseItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: importCauseItemId, causeName: "Mia Young (S8-352)"));
        var csoId = SeedSyncCauseSummary(importCauseItemId, ObjectChangeType.Added);
        SeedImportEvent(csoId);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var member = chain.Cohorts.Single().Members.Single();
        Assert.That(member.Resolution, Is.EqualTo(CausalChainResolution.NoFurtherCauses));
        Assert.That(member.Causes, Is.Empty);
    }

    /// <summary>
    /// A record whose imports have aged out yields no hop, and the chain ends at the synchronisation as a
    /// complete story rather than pretending the timeline never existed or faking a truncation.
    /// </summary>
    /// <remarks>
    /// Uses AttributeFlow rather than the default Projected: a Projected/Joined/Created cause always
    /// resolves now, because it carries the derived Identity-creation cohort (#1495 follow-up) regardless
    /// of whether an import is retained. AttributeFlow is still a source-import-hop item type, so this
    /// keeps testing exactly what it always tested: the record's own timeline, aged out.
    /// </remarks>
    [Test]
    public async Task GetCausalChain_SynchronisationCauseWithNoRetainedImport_EndsThereAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSyncCauseSummary(syncItemId, ObjectChangeType.AttributeFlow);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var member = chain.Cohorts.Single().Members.Single();
        Assert.That(member.Resolution, Is.EqualTo(CausalChainResolution.NoFurtherCauses));
    }

    private void SeedSeveredSyncCauseSummary(
        Guid itemId, string? externalIdSnapshot = "S8-352", int? connectedSystemId = 1)
    {
        _retainedItemIds.Add(itemId);
        _summariesById[itemId] = new CausalChainItemSummary
        {
            Id = itemId,
            ObjectChangeType = ObjectChangeType.Disconnected,
            // The severed shape (#1495): the record's deletion nulled the id the timeline is walked on.
            ConnectedSystemObjectId = null,
            ConnectedSystemId = connectedSystemId,
            ExternalIdSnapshot = externalIdSnapshot,
            ActivityExecuted = new DateTime(2026, 8, 15, 11, 0, 0, DateTimeKind.Utc)
        };
    }

    private Guid SeedImportEventForExternalId(
        string externalId, int connectedSystemId = 1, ObjectChangeType changeType = ObjectChangeType.Deleted)
    {
        var importItemId = Guid.NewGuid();
        _retainedItemIds.Add(importItemId);
        _importEventsByExternalId[(connectedSystemId, externalId)] = new CausalSourceImportEvent
        {
            RunProfileExecutionItemId = importItemId,
            ChangeType = changeType,
            DisplayName = "Mia Young (S8-352)",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = "Yellowstone APAC"
        };
        return importItemId;
    }

    /// <summary>
    /// The deletion-cascade shape (#1495): a deprovision's chain runs back to the synchronisation that
    /// staged the delete, whose record was hard-deleted in the same cascade, nulling the id its timeline is
    /// walked on. The external ID snapshotted on the items survives the deletion and reaches the same
    /// import, so the chain still ends at the source deletion that truly started the story.
    /// </summary>
    [Test]
    public async Task GetCausalChain_SynchronisationCauseWhoseRecordWasDeleted_ContinuesViaTheExternalIdSnapshotAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSeveredSyncCauseSummary(syncItemId);
        var importItemId = SeedImportEventForExternalId("S8-352");

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var syncMember = chain.Cohorts.Single().Members.Single();
        Assert.That(syncMember.Resolution, Is.EqualTo(CausalChainResolution.Resolved),
            "the record's deletion must not sever the very chain that explains the deletion");
        var sourceHop = syncMember.Causes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceHop.SourceImportChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(sourceHop.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(sourceHop.Members.Single().RunProfileExecutionItemId, Is.EqualTo(importItemId));
        }
    }

    /// <summary>
    /// The same shape entered from the synchronisation item itself must read identically: the snapshot key
    /// serves the root hop exactly as the record id would have.
    /// </summary>
    [Test]
    public async Task GetCausalChain_SeveredSynchronisationItemViewedDirectly_GetsTheSnapshotHopAtRootAsync()
    {
        var itemId = Guid.NewGuid();
        SeedSeveredSyncCauseSummary(itemId);
        SeedImportEventForExternalId("S8-352");

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts, Has.Count.EqualTo(1));
        Assert.That(chain.Cohorts[0].SourceImportChangeType, Is.EqualTo(ObjectChangeType.Deleted));
    }

    /// <summary>
    /// Where even the snapshot reaches no import, a synchronisation-side item with a deleted record must
    /// end as history lost, not as a complete story: the item was fed by an import by definition, so
    /// "nothing caused this" would tell the reader they had the whole story when the deletion cut it short.
    /// A live record with no retained import keeps the complete-story ending it always had (the test
    /// above); only the severed shape is known to have lost information.
    /// </summary>
    [Test]
    public async Task GetCausalChain_SeveredTimelineWithNoReachableImport_ReportsTheHistoryAsNotRetainedAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSeveredSyncCauseSummary(syncItemId);
        // Deliberately no import event seeded: the snapshot lookup finds nothing either.

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts.Single().Members.Single().Resolution,
            Is.EqualTo(CausalChainResolution.CauseNotRetained));
    }

    /// <summary>
    /// A severed item that recorded no snapshot at all (legacy history) has nothing to look up, and must
    /// still end as history lost rather than complete.
    /// </summary>
    [Test]
    public async Task GetCausalChain_SeveredTimelineWithNoSnapshot_StillReportsTheHistoryAsNotRetainedAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSeveredSyncCauseSummary(syncItemId, externalIdSnapshot: null, connectedSystemId: null);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts.Single().Members.Single().Resolution,
            Is.EqualTo(CausalChainResolution.CauseNotRetained));
    }

    #endregion

    #region the Identity-creation cohort (#1495 follow-up)

    /// <summary>
    /// A resolved projecting cause states its own creation as a derived cohort attached under the
    /// member, so the Lineage view's Identity column can say the Identity was created even when the
    /// projecting item lies further back than the page's own root.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ResolvedProjectionCause_AddsAnIdentityCreationCohortUnderTheMemberAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSyncCauseSummary(syncItemId, ObjectChangeType.Projected);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var syncMember = chain.Cohorts.Single().Members.Single();
        var creationCohort = syncMember.Causes.Single(c => c.MetaverseChangeType != null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(creationCohort.MetaverseChangeType, Is.EqualTo(ObjectChangeType.Projected));
            Assert.That(creationCohort.Members.Single().DisplayName, Is.EqualTo("Mia Young (S8-352)"));
            Assert.That(creationCohort.Members.Single().Occurred,
                Is.EqualTo(new DateTime(2026, 8, 15, 11, 0, 0, DateTimeKind.Utc)));
            Assert.That(creationCohort.Members.Single().RunProfileExecutionItemId, Is.EqualTo(syncItemId));
            Assert.That(creationCohort.Members.Single().Resolution, Is.EqualTo(CausalChainResolution.Resolved));
        }
    }

    /// <summary>
    /// The join variant reads identically: a resolved joining cause states the Identity was joined to,
    /// not projected.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ResolvedJoinCause_AddsAnIdentityCreationCohortWithJoinedTypeAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSyncCauseSummary(syncItemId, ObjectChangeType.Joined);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var syncMember = chain.Cohorts.Single().Members.Single();
        var creationCohort = syncMember.Causes.Single(c => c.MetaverseChangeType != null);
        Assert.That(creationCohort.MetaverseChangeType, Is.EqualTo(ObjectChangeType.Joined));
    }

    /// <summary>
    /// A non-creating sync type (an ordinary Attribute Flow) says nothing about the Identity's origin,
    /// so it must add no creation cohort at all.
    /// </summary>
    [Test]
    public async Task GetCausalChain_NonCreatingSyncCause_AddsNoIdentityCreationCohortAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSyncCauseSummary(syncItemId, ObjectChangeType.AttributeFlow);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var syncMember = chain.Cohorts.Single().Members.Single();
        Assert.That(syncMember.Causes.Any(c => c.MetaverseChangeType != null), Is.False);
    }

    /// <summary>
    /// The projecting item viewed directly gets no creation cohort of its own: this-run's events already
    /// state "Identity created" on the Identity column via <see cref="JIM.Web.Causality.CausalityLane.Identity"/>
    /// in that case, so a derived cohort here would say the same thing twice.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ProjectionItemViewedDirectly_AddsNoCreationCohortAtRootAsync()
    {
        var itemId = Guid.NewGuid();
        SeedSyncCauseSummary(itemId, ObjectChangeType.Projected);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        Assert.That(chain.Cohorts.Any(c => c.MetaverseChangeType != null), Is.False);
    }

    /// <summary>
    /// The same projecting item reached on two branches (a cohort of two members both pointing at it)
    /// must still read as one "Identity created" card, not one per branch.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ProjectorReachedOnTwoBranches_ProducesOneCreationCohortCardAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId,
            NewEdge(itemId, causeItemId: syncItemId, causeName: "Branch A"),
            NewEdge(itemId, causeItemId: syncItemId, causeName: "Branch B"));
        SeedSyncCauseSummary(syncItemId, ObjectChangeType.Projected);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var creationCohorts = chain.Cohorts.SelectMany(c => c.Members)
            .SelectMany(m => m.Causes)
            .Where(c => c.MetaverseChangeType != null)
            .ToList();
        Assert.That(creationCohorts, Has.Count.EqualTo(1),
            "the same projecting item reached on two branches must still read as one 'Identity created' card");
    }

    /// <summary>
    /// The creation cohort's member states a fact about the item it is attached under; it must not be
    /// walked again as though it were a fresh cause, or the walk would re-query the same item every
    /// level and never terminate on its own.
    /// </summary>
    [Test]
    public async Task GetCausalChain_IdentityCreationCohort_DoesNotReenterTheWalkAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSyncCauseSummary(syncItemId, ObjectChangeType.Projected);

        var chain = await _application.Activities.GetCausalChainAsync(itemId, maxDepth: 5);

        var creationMember = chain.Cohorts.Single().Members.Single().Causes
            .Single(c => c.MetaverseChangeType != null).Members.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(creationMember.Causes, Is.Empty,
                "the creation cohort states a fact about the item itself; it must not be walked again as its own cause");
            Assert.That(chain.IsTruncatedByDepth, Is.False);
        }
        _mockActivityRepository.Verify(
            r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()),
            Times.Exactly(2),
            "the creation cohort's member must not be re-queried as though it were a fresh cause");
    }

    #endregion

    #region export confirmation

    /// <summary>
    /// The confirmation-to-export hop (#1528). The edge a confirming import records names the Pending Export
    /// it confirms and nothing else, deliberately: reconciliation deletes that Pending Export moments later,
    /// and pairing a confirmation with an export by Connected System Object id alone can land on the wrong
    /// cycle, because an object cycles through export and import repeatedly. The Pending Export id IS the
    /// cycle, which is what makes it the safe key.
    ///
    /// So the walk has to spend it. The export execution's own edge carries the same Pending Export id, so
    /// the pair identifies the executing item exactly; without following it, every confirming import reports
    /// "No earlier causes recorded" while the export, the synchronisation that staged it and the import that
    /// started the whole thing all sit recorded and unreachable.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ConfirmingImport_ReachesTheExportThroughThePendingExportIdAsync()
    {
        var confirmingItemId = Guid.NewGuid();
        var exportItemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        var pendingExportId = Guid.NewGuid();

        SeedEdges(confirmingItemId, NewConfirmationEdge(confirmingItemId, pendingExportId, "Ada Lovelace"));
        SeedEdges(exportItemId, NewQueueingEdge(exportItemId, pendingExportId, syncItemId, "Ada Lovelace"));
        SeedExportExecutionItem(pendingExportId, exportItemId);
        Retain(exportItemId);

        var chain = await _application.Activities.GetCausalChainAsync(confirmingItemId);

        var confirmation = chain.Cohorts.Single().Members.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(confirmation.RunProfileExecutionItemId, Is.EqualTo(exportItemId),
                "the export that caused the confirmation is identified by the Pending Export they share");
            Assert.That(confirmation.Resolution, Is.EqualTo(CausalChainResolution.Resolved),
                "a hop the walk can follow is not an ending; reporting one hides the whole chain behind it");
            Assert.That(confirmation.Causes.Single().Members.Single().RunProfileExecutionItemId,
                Is.EqualTo(syncItemId),
                "and the export's own cause must follow, which is the point of resolving the hop at all");
        }
    }

    /// <summary>
    /// The export has aged out while the confirmation survives, which is the ordinary shape once a deployment
    /// outlives one retention window: causes are always older than their effects. It must read as history
    /// lost, never as the complete story.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ConfirmingImportWhoseExportAgedOut_ReportsTheHistoryAsNotRetainedAsync()
    {
        var confirmingItemId = Guid.NewGuid();
        var exportItemId = Guid.NewGuid();
        var pendingExportId = Guid.NewGuid();

        SeedEdges(confirmingItemId, NewConfirmationEdge(confirmingItemId, pendingExportId, "Ada Lovelace"));
        SeedExportExecutionItem(pendingExportId, exportItemId);
        // Deliberately not retained: the edge still resolves an id, and the item behind it is gone.

        var chain = await _application.Activities.GetCausalChainAsync(confirmingItemId);

        Assert.That(chain.Cohorts.Single().Members.Single().Resolution,
            Is.EqualTo(CausalChainResolution.CauseNotRetained));
    }

    /// <summary>
    /// Nothing to resolve to. A Pending Export whose export execution recorded no edge (an export that
    /// failed, so the queueing cause was never written) leaves the confirmation genuinely terminal, and it
    /// must keep saying so rather than inventing a hop.
    /// </summary>
    [Test]
    public async Task GetCausalChain_ConfirmingImportWithNoMatchingExportEdge_StaysACompleteEndingAsync()
    {
        var confirmingItemId = Guid.NewGuid();
        SeedEdges(confirmingItemId, NewConfirmationEdge(confirmingItemId, Guid.NewGuid(), "Ada Lovelace"));

        var chain = await _application.Activities.GetCausalChainAsync(confirmingItemId);

        var confirmation = chain.Cohorts.Single().Members.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(confirmation.RunProfileExecutionItemId, Is.Null);
            Assert.That(confirmation.Resolution, Is.EqualTo(CausalChainResolution.NoFurtherCauses));
        }
    }

    #endregion

    private void SeedEdges(Guid effectItemId, params CausalEdge[] edges)
    {
        if (!_edgesByEffectItemId.TryGetValue(effectItemId, out var list))
        {
            list = [];
            _edgesByEffectItemId[effectItemId] = list;
        }

        list.AddRange(edges);
    }

    private void Retain(params Guid[] itemIds)
    {
        foreach (var id in itemIds)
            _retainedItemIds.Add(id);
    }

    /// <summary>Records which item executed the export a Pending Export was staged for.</summary>
    private void SeedExportExecutionItem(Guid pendingExportId, Guid exportItemId)
    {
        _exportItemIdsByPendingExportId[pendingExportId] = exportItemId;
    }

    /// <summary>
    /// The edge a confirming import records: it names the Pending Export it confirms and never an item, so
    /// the walk has nothing to follow until it spends that id.
    /// </summary>
    private static CausalEdge NewConfirmationEdge(Guid effectItemId, Guid pendingExportId, string causeName)
    {
        return new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectItemId,
            EffectSyncOutcomeId = Guid.NewGuid(),
            CausePendingExportId = pendingExportId,
            CauseConnectedSystemObjectId = Guid.NewGuid(),
            CauseDisplayName = causeName,
            EdgeType = CausalEdgeType.ExportCausedImportConfirmation,
            ConnectedSystemId = 7,
            ConnectedSystemName = "Yellowstone APAC"
        };
    }

    /// <summary>
    /// The edge an export execution records, carrying the same Pending Export id. It is what makes the
    /// confirmation's id resolvable, and it names the synchronisation that staged the export.
    /// </summary>
    private static CausalEdge NewQueueingEdge(Guid effectItemId, Guid pendingExportId, Guid causeItemId, string causeName)
    {
        return new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectItemId,
            EffectSyncOutcomeId = Guid.NewGuid(),
            CauseRunProfileExecutionItemId = causeItemId,
            CausePendingExportId = pendingExportId,
            CauseDisplayName = causeName,
            EdgeType = CausalEdgeType.PendingExportQueueingCausedExportExecution,
            ReasonCode = CausalReasonCode.ExportDeleteStaged,
            ConnectedSystemId = 7,
            ConnectedSystemName = "Yellowstone APAC"
        };
    }

    private static CausalEdge NewEdge(
        Guid effectItemId,
        Guid? effectOutcomeId = null,
        Guid? causeItemId = null,
        string? causeName = null,
        CausalReasonCode reasonCode = CausalReasonCode.AllAuthoritativeSourcesDisconnected)
    {
        return new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectItemId,
            EffectSyncOutcomeId = effectOutcomeId,
            CauseRunProfileExecutionItemId = causeItemId,
            CauseMetaverseObjectId = Guid.NewGuid(),
            CauseDisplayName = causeName,
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ReasonCode = reasonCode,
            ConnectedSystemId = 7,
            ConnectedSystemName = "Yellowstone APAC"
        };
    }
}
