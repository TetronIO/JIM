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

    [SetUp]
    public void SetUp()
    {
        _edgesByEffectItemId.Clear();
        _retainedItemIds.Clear();
        _summariesById.Clear();
        _importEventsByCsoId.Clear();

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
            .Setup(r => r.GetLatestImportItemForCsoAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<Guid>()))
            .ReturnsAsync((Guid csoId, DateTime _, Guid _) => _importEventsByCsoId.GetValueOrDefault(csoId));

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
            ConnectedSystemName = "Yellowstone APAC"
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
        var sourceHop = syncMember.Causes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(sourceHop.SourceImportChangeType, Is.EqualTo(ObjectChangeType.Added));
            Assert.That(sourceHop.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(sourceHop.Members.Single().RunProfileExecutionItemId, Is.EqualTo(importItemId),
                "the hop must link to the import item so the walk and the reader can continue there");
            Assert.That(sourceHop.Members.Single().DisplayName, Is.EqualTo("Mia Young (S8-352)"));
            Assert.That(sourceHop.Members.Single().Resolution, Is.EqualTo(CausalChainResolution.NoFurtherCauses),
                "an import with no edges of its own is the true root: data arrived from the source system");
        });
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
    [Test]
    public async Task GetCausalChain_SynchronisationCauseWithNoRetainedImport_EndsThereAsync()
    {
        var itemId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        SeedEdges(itemId, NewEdge(itemId, causeItemId: syncItemId, causeName: "Mia Young (S8-352)"));
        SeedSyncCauseSummary(syncItemId);

        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var member = chain.Cohorts.Single().Members.Single();
        Assert.That(member.Resolution, Is.EqualTo(CausalChainResolution.NoFurtherCauses));
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
