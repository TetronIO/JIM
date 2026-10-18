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
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The downward causal walk (#1811): what an item went on to cause, assembled level by level from the repository's
/// grouped reads. The repository is faked by a small consequence graph that groups exactly as the SQL does (by parent
/// and shared values), so these tests are about the walk itself: nesting, endings, the read bounds, the waiting flag,
/// round trips, and paging a cohort by its key.
/// </summary>
[TestFixture]
public class CausalConsequenceWalkTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    private readonly List<GraphHop> _hops = [];
    private readonly Dictionary<Guid, CausalConsequenceEndingCount> _leafEndings = new();

    private sealed record GraphHop(Guid CauseItemId, CausalConsequenceCohortTuple Tuple, CausalConsequenceMember Member, bool Continues);

    private static readonly CausalConsequenceCohortTuple RemovalTuple = new(CausalConsequenceHopKind.Edge,
        CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, CausalReasonCode.AuthoritativeSourceDisconnected,
        1, null, "Group", "Static Members", ObjectChangeType.PendingExport, null, null);

    private static CausalConsequenceCohortTuple ExportTuple(string outcomeSummary) => new(CausalConsequenceHopKind.Edge,
        CausalEdgeType.PendingExportQueueingCausedExportExecution, CausalReasonCode.ExportUpdateStaged,
        2, null, "group", null, ObjectChangeType.Exported, outcomeSummary, null);

    private static readonly CausalConsequenceCohortTuple ConfirmationTuple = new(CausalConsequenceHopKind.Confirmation,
        CausalEdgeType.ExportCausedImportConfirmation, CausalReasonCode.NotSet, 2, null, "group", null, ObjectChangeType.Updated, null, null);

    private static CausalConsequenceCohortTuple OutstandingTuple(PendingExportStatus status) => new(
        CausalConsequenceHopKind.OutstandingPendingExport, null, CausalReasonCode.NotSet, 2, null, null, null, null, null, status);

    private static readonly CausalConsequenceCohortTuple ScheduledDeletionTuple = new(CausalConsequenceHopKind.ScheduledDeletion,
        null, CausalReasonCode.NotSet, null, null, null, null, null, null, null);

    private static readonly CausalConsequenceCohortTuple HousekeepingTuple = new(CausalConsequenceHopKind.Edge,
        CausalEdgeType.MetaverseObjectDeletionScheduleCausedDeletion, CausalReasonCode.AuthoritativeSourceDisconnected,
        1, null, "User", null, ObjectChangeType.Deleted, "MvoDeleted:1", null);

    [SetUp]
    public void SetUp()
    {
        _hops.Clear();
        _leafEndings.Clear();

        _mockRepository = new Mock<IRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);

        _mockActivityRepository
            .Setup(r => r.GetCausalConsequenceLevelAsync(It.IsAny<IReadOnlyList<CausalConsequenceFrontierMember>>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((IReadOnlyList<CausalConsequenceFrontierMember> frontier, int pageSize, int limit) => ReadLevel(frontier, pageSize, limit));

        _mockActivityRepository
            .Setup(r => r.GetCausalConsequenceCohortMembersAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CausalConsequenceCohortTuple>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync((IReadOnlyList<Guid> frontier, CausalConsequenceCohortTuple tuple, int skip, int take, string? search) =>
            {
                var matching = _hops
                    .Where(h => frontier.Contains(h.CauseItemId) && h.Tuple == tuple)
                    .Select(h => h.Member)
                    .Where(m => search == null || (m.DisplayName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.Occurred).ThenBy(m => m.DisplayName)
                    .ToList();
                return (matching.Skip(skip).Take(take).ToList(), matching.Count);
            });

        _mockActivityRepository
            .Setup(r => r.GetCausalConsequenceRepresentativesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids.ToDictionary(id => id, id => new ActivityRunProfileExecutionItem { Id = id }));

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    // ─── Endings at the root ───

    [Test]
    public async Task GetCausalConsequences_ItemThatCausedNothing_EndsWithNoFurtherConsequencesAsync()
    {
        var item = Guid.NewGuid();

        var consequences = await _application.Activities.GetCausalConsequencesAsync(item);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(consequences.RunProfileExecutionItemId, Is.EqualTo(item));
            Assert.That(consequences.Cohorts, Is.Empty);
            Assert.That(consequences.Endings.Single().Ending, Is.EqualTo(CausalConsequenceEnding.NoFurtherConsequences));
            Assert.That(consequences.HasWaitingBranches, Is.False);
            Assert.That(consequences.IsTruncatedByDepth, Is.False);
            Assert.That(consequences.CheckedAt, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)));
        }
    }

    [Test]
    public async Task GetCausalConsequences_ImportNoSynchronisationHasActedOn_IsWaitingAndNamesTheSystemAsync()
    {
        var import = Guid.NewGuid();
        _leafEndings[import] = new CausalConsequenceEndingCount
        {
            Ending = CausalConsequenceEnding.WaitingForSynchronisation, MemberCount = 1, ConnectedSystemId = 1,
            ConnectedSystemName = "Yellowstone APAC"
        };

        var consequences = await _application.Activities.GetCausalConsequencesAsync(import);

        var ending = consequences.Endings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ending.Ending, Is.EqualTo(CausalConsequenceEnding.WaitingForSynchronisation));
            Assert.That(ending.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(consequences.HasWaitingBranches, Is.True, "a Synchronisation will act on it without anyone doing anything");
        }
    }

    [Test]
    public async Task GetCausalConsequences_ImportThatNeededNothingDoing_IsSettledAsync()
    {
        var import = Guid.NewGuid();
        _leafEndings[import] = new CausalConsequenceEndingCount { Ending = CausalConsequenceEnding.NothingFollowed, MemberCount = 1 };

        var consequences = await _application.Activities.GetCausalConsequencesAsync(import);

        Assert.That(consequences.Endings.Single().Ending, Is.EqualTo(CausalConsequenceEnding.NothingFollowed));
        Assert.That(consequences.HasWaitingBranches, Is.False);
    }

    // ─── The cascade ───

    [Test]
    public async Task GetCausalConsequences_MidCascade_EndsTheQueuedBranchWaitingForAnExportAsync()
    {
        var sync = Guid.NewGuid();
        AddHop(sync, OutstandingTuple(PendingExportStatus.Pending), Pseudo("Tina Adams"));
        AddHop(sync, OutstandingTuple(PendingExportStatus.ExportNotConfirmed), Pseudo("Tina Adams", due: T0.AddHours(2)));
        AddHop(sync, OutstandingTuple(PendingExportStatus.Parked), Pseudo("Tina Adams"));

        var consequences = await _application.Activities.GetCausalConsequencesAsync(sync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(consequences.Cohorts.Select(c => c.Ending), Is.EquivalentTo(new CausalConsequenceEnding?[]
            {
                CausalConsequenceEnding.WaitingForExport, CausalConsequenceEnding.WaitingForRetry, CausalConsequenceEnding.NeedsAttention
            }));
            Assert.That(consequences.Cohorts.Single(c => c.Ending == CausalConsequenceEnding.WaitingForRetry).DueAt,
                Is.EqualTo(T0.AddHours(2)));
            Assert.That(consequences.HasWaitingBranches, Is.True);
            Assert.That(consequences.Endings, Is.Empty, "the item led somewhere, so it has no ending of its own");
        }
    }

    [Test]
    public async Task GetCausalConsequences_CompleteCascade_NestsEachLevelUnderTheCohortThatCausedItAsync()
    {
        var deletion = Guid.NewGuid();
        var removal = Item("Project Pulse");
        AddHop(deletion, RemovalTuple, removal);
        var export = Item("Project Pulse", T0.AddHours(1));
        AddHop(removal.RunProfileExecutionItemId!.Value, ExportTuple("Exported:1"), export);
        var confirmation = Item("Project Pulse", T0.AddHours(2));
        AddHop(export.RunProfileExecutionItemId!.Value, ConfirmationTuple, confirmation, continues: false);

        var consequences = await _application.Activities.GetCausalConsequencesAsync(deletion);

        var removals = consequences.Cohorts.Single();
        var exports = removals.Consequences.Single();
        var confirmations = exports.Consequences.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removals.AttributeName, Is.EqualTo("Static Members"));
            Assert.That(removals.Ending, Is.Null, "it carries on");
            Assert.That(exports.EdgeType, Is.EqualTo(CausalEdgeType.PendingExportQueueingCausedExportExecution));
            Assert.That(confirmations.Ending, Is.EqualTo(CausalConsequenceEnding.Confirmed));
            Assert.That(confirmations.Consequences, Is.Empty, "a confirmation ends its branch");
            Assert.That(confirmations.Endings, Is.Empty, "and is not walked into");
            Assert.That(consequences.HasWaitingBranches, Is.False);
            Assert.That(consequences.IsTruncatedByDepth, Is.False);
            Assert.That(new[] { removals.CohortKey, exports.CohortKey, confirmations.CohortKey }.Distinct().Count(), Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetCausalConsequences_CohortsOfItems_CarryTheirFirstMemberAsARepresentativeFromOneReadAsync()
    {
        // The portal places a consequence's cards from what its items recorded, so each cohort of items carries one of
        // them whole. One read for the whole walk, not one per cohort or per level.
        var deletion = Guid.NewGuid();
        var pulse = Item("Project Pulse");
        var atlas = Item("Project Atlas");
        AddHop(deletion, RemovalTuple, pulse);
        AddHop(deletion, RemovalTuple, atlas);
        var export = Item("Project Pulse", T0.AddHours(1));
        AddHop(pulse.RunProfileExecutionItemId!.Value, ExportTuple("Exported:1"), export);
        AddHop(atlas.RunProfileExecutionItemId!.Value, OutstandingTuple(PendingExportStatus.Pending), Pseudo("Project Atlas"));

        var consequences = await _application.Activities.GetCausalConsequencesAsync(deletion);

        var removals = consequences.Cohorts.Single();
        var exports = removals.Consequences.Single(c => c.HopKind == CausalConsequenceHopKind.Edge);
        var outstanding = removals.Consequences.Single(c => c.HopKind == CausalConsequenceHopKind.OutstandingPendingExport);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removals.Representative?.Id, Is.EqualTo(atlas.RunProfileExecutionItemId), "the first member, by name at a shared time");
            Assert.That(exports.Representative?.Id, Is.EqualTo(export.RunProfileExecutionItemId));
            Assert.That(outstanding.Representative, Is.Null, "a cohort of Pending Exports has no item to show");
        }
        _mockActivityRepository.Verify(r => r.GetCausalConsequenceRepresentativesAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Once);
    }

    [Test]
    public async Task GetCausalConsequences_NoCohortsOfItems_DoesNotReadRepresentativesAsync()
    {
        await _application.Activities.GetCausalConsequencesAsync(Guid.NewGuid());

        _mockActivityRepository.Verify(r => r.GetCausalConsequenceRepresentativesAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
    }

    [Test]
    public async Task GetCausalConsequences_LargeCohortWithAFailedSplit_CountsExactlyAndPagesTheFirstMembersAsync()
    {
        var deletion = Guid.NewGuid();
        var removals = new List<CausalConsequenceMember>();
        for (var i = 0; i < 4_218; i++)
        {
            var removal = Item($"Group {i:D4}");
            AddHop(deletion, RemovalTuple, removal);
            removals.Add(removal);
        }
        foreach (var removal in removals.Take(4_216))
            AddHop(removal.RunProfileExecutionItemId!.Value, ExportTuple("Exported:1"), Item(removal.DisplayName!, T0.AddHours(1)));
        foreach (var removal in removals.Skip(4_216))
            AddHop(removal.RunProfileExecutionItemId!.Value, ExportTuple("ExportFailed:1"), Item(removal.DisplayName!, T0.AddHours(1)));

        var consequences = await _application.Activities.GetCausalConsequencesAsync(deletion);

        var removalCohort = consequences.Cohorts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removalCohort.MemberCount, Is.EqualTo(4_218));
            Assert.That(removalCohort.Members, Has.Count.EqualTo(50), "the first page only");
            Assert.That(removalCohort.Consequences.Select(c => (c.OutcomeSummary, c.MemberCount)),
                Is.EquivalentTo(new[] { ("Exported:1", 4_216), ("ExportFailed:1", 2) }));
        }
    }

    [Test]
    public async Task GetCausalConsequences_SomeMembersLedNowhere_CountsThemOnTheirCohortAsync()
    {
        var deletion = Guid.NewGuid();
        var removals = Enumerable.Range(0, 3).Select(i => Item($"Group {i}")).ToList();
        foreach (var removal in removals)
            AddHop(deletion, RemovalTuple, removal);
        AddHop(removals[0].RunProfileExecutionItemId!.Value, ExportTuple("Exported:1"), Item("Group 0", T0.AddHours(1)));

        var consequences = await _application.Activities.GetCausalConsequencesAsync(deletion);

        var cohort = consequences.Cohorts.Single();
        Assert.That(cohort.Consequences, Has.Count.EqualTo(1));
        Assert.That(cohort.Endings.Single().Ending, Is.EqualTo(CausalConsequenceEnding.NoFurtherConsequences));
        Assert.That(cohort.Endings.Single().MemberCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetCausalConsequences_GracePeriodDeletion_ShowsTheScheduleAndThenTheHousekeepingDeletionAsync()
    {
        var scheduling = Guid.NewGuid();
        var pending = Guid.NewGuid();
        AddHop(pending, ScheduledDeletionTuple, Pseudo("Tina Adams", due: T0.AddDays(7)));

        var waiting = await _application.Activities.GetCausalConsequencesAsync(pending);

        Assert.That(waiting.Cohorts.Single().Ending, Is.EqualTo(CausalConsequenceEnding.DeletionScheduled));
        Assert.That(waiting.Cohorts.Single().DueAt, Is.EqualTo(T0.AddDays(7)));
        Assert.That(waiting.HasWaitingBranches, Is.True);

        var housekeeping = Item("Tina Adams", T0.AddDays(7));
        AddHop(scheduling, HousekeepingTuple, housekeeping);
        var removal = Item("Project Pulse", T0.AddDays(7));
        AddHop(housekeeping.RunProfileExecutionItemId!.Value, RemovalTuple, removal);

        var carriedOut = await _application.Activities.GetCausalConsequencesAsync(scheduling);

        var deletion = carriedOut.Cohorts.Single();
        Assert.That(deletion.EdgeType, Is.EqualTo(CausalEdgeType.MetaverseObjectDeletionScheduleCausedDeletion));
        Assert.That(deletion.Consequences.Single().AttributeName, Is.EqualTo("Static Members"),
            "the walk carries on through the housekeeping deletion into what it caused");
    }

    // ─── Read bounds ───

    [Test]
    public async Task GetCausalConsequences_ChainLongerThanTheBound_StopsAndSaysSoAsync()
    {
        var item = Guid.NewGuid();
        var cause = item;
        for (var i = 0; i < 4; i++)
        {
            var next = Item($"Step {i}", T0.AddHours(i));
            AddHop(cause, ExportTuple($"Step:{i}"), next);
            cause = next.RunProfileExecutionItemId!.Value;
        }

        var consequences = await _application.Activities.GetCausalConsequencesAsync(item, maxDepth: 2);

        var second = consequences.Cohorts.Single().Consequences.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Consequences, Is.Empty, "nothing past the bound is read");
            Assert.That(second.IsFurtherThanShown, Is.True);
            Assert.That(consequences.IsTruncatedByDepth, Is.True);
        }
    }

    [Test]
    public async Task GetCausalConsequences_ChainEndingExactlyAtTheBound_IsNotReportedAsTruncatedAsync()
    {
        // The bound must not claim more exists when it does not: the walk checks one level past it.
        var item = Guid.NewGuid();
        var first = Item("Step 0");
        AddHop(item, ExportTuple("Step:0"), first);
        var second = Item("Step 1", T0.AddHours(1));
        AddHop(first.RunProfileExecutionItemId!.Value, ExportTuple("Step:1"), second);

        var consequences = await _application.Activities.GetCausalConsequencesAsync(item, maxDepth: 2);

        var last = consequences.Cohorts.Single().Consequences.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.IsFurtherThanShown, Is.False);
            Assert.That(last.Endings.Single().Ending, Is.EqualTo(CausalConsequenceEnding.NoFurtherConsequences));
            Assert.That(consequences.IsTruncatedByDepth, Is.False);
        }
    }

    [Test]
    public async Task GetCausalConsequences_ManyLevels_ReadsOncePerLevelPlusTheBoundCheckAsync()
    {
        var item = Guid.NewGuid();
        var cause = item;
        for (var i = 0; i < 3; i++)
        {
            var members = Enumerable.Range(0, 20).Select(j => Item($"Step {i}.{j}", T0.AddHours(i))).ToList();
            foreach (var member in members)
                AddHop(cause, ExportTuple($"Step:{i}"), member);
            cause = members[0].RunProfileExecutionItemId!.Value;
        }

        await _application.Activities.GetCausalConsequencesAsync(item, maxDepth: 6);

        // Three levels with consequences, then a fourth that finds none: one read each, however many members.
        _mockActivityRepository.Verify(r => r.GetCausalConsequenceLevelAsync(
            It.IsAny<IReadOnlyList<CausalConsequenceFrontierMember>>(), It.IsAny<int>(), It.IsAny<int>()), Times.Exactly(4));
    }

    // ─── Paging a cohort ───

    [Test]
    public async Task GetCausalConsequenceCohortMembers_NestedCohortsKey_PagesThatCohortsMembersAsync()
    {
        var deletion = Guid.NewGuid();
        var removal = Item("Project Pulse");
        AddHop(deletion, RemovalTuple, removal);
        var exports = Enumerable.Range(0, 5).Select(i => Item($"Account {i}", T0.AddHours(1))).ToList();
        foreach (var export in exports)
            AddHop(removal.RunProfileExecutionItemId!.Value, ExportTuple("Exported:1"), export);
        var consequences = await _application.Activities.GetCausalConsequencesAsync(deletion);
        var exportCohort = consequences.Cohorts.Single().Consequences.Single();

        var page = await _application.Activities.GetCausalConsequenceCohortMembersAsync(deletion, exportCohort.CohortKey, page: 2, pageSize: 2);
        var found = await _application.Activities.GetCausalConsequenceCohortMembersAsync(deletion, exportCohort.CohortKey, 1, 10, search: "account 4");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page!.TotalResults, Is.EqualTo(5));
            Assert.That(page!.CurrentPage, Is.EqualTo(2));
            Assert.That(page!.Results.Select(m => m.DisplayName), Is.EqualTo(new[] { "Account 2", "Account 3" }));
            Assert.That(found!.Results.Single().DisplayName, Is.EqualTo("Account 4"));
        }
    }

    [TestCase("")]
    [TestCase("not a key")]
    [TestCase("bm90IGpzb24")]
    public async Task GetCausalConsequenceCohortMembers_KeyThatIsNotOne_ReturnsNullAsync(string cohortKey)
    {
        var page = await _application.Activities.GetCausalConsequenceCohortMembersAsync(Guid.NewGuid(), cohortKey, 1, 10);

        Assert.That(page, Is.Null);
    }

    // ─── The fake graph ───

    private void AddHop(Guid cause, CausalConsequenceCohortTuple tuple, CausalConsequenceMember member, bool? continues = null)
    {
        var defaultContinues = tuple.HopKind is CausalConsequenceHopKind.Edge or CausalConsequenceHopKind.NextSynchronisation;
        _hops.Add(new GraphHop(cause, tuple, member, continues ?? defaultContinues));
    }

    private static CausalConsequenceMember Item(string name, DateTime? occurred = null) => new()
    {
        RunProfileExecutionItemId = Guid.NewGuid(),
        DisplayName = name,
        Occurred = occurred ?? T0
    };

    private static CausalConsequenceMember Pseudo(string name, DateTime? due = null) => new()
    {
        PendingExportId = Guid.NewGuid(),
        DisplayName = name,
        Occurred = T0,
        DueAt = due
    };

    /// <summary>
    /// Groups the graph's hops out of the frontier as the SQL does: by parent cohort and shared values, members earliest
    /// first then by name, continuing items capped; frontier items with no hop become leaves.
    /// </summary>
    private CausalConsequenceLevel ReadLevel(IReadOnlyList<CausalConsequenceFrontierMember> frontier, int pageSize, int limit)
    {
        var level = new CausalConsequenceLevel();
        var groups = frontier
            .SelectMany(f => _hops.Where(h => h.CauseItemId == f.RunProfileExecutionItemId).Select(h => (f.ParentCohortOrdinal, Hop: h)))
            .GroupBy(x => (x.ParentCohortOrdinal, x.Hop.Tuple))
            .OrderBy(g => g.Key.ParentCohortOrdinal).ThenBy(g => g.Min(x => x.Hop.Member.Occurred));
        foreach (var group in groups)
        {
            var members = group.Select(x => x.Hop).OrderBy(h => h.Member.Occurred).ThenBy(h => h.Member.DisplayName).ToList();
            var continuing = members.Where(h => h.Continues).Select(h => h.Member.RunProfileExecutionItemId!.Value).ToList();
            level.Cohorts.Add(new CausalConsequenceCohortRow
            {
                Ordinal = level.Cohorts.Count,
                ParentCohortOrdinal = group.Key.ParentCohortOrdinal,
                Tuple = group.Key.Tuple,
                MemberCount = members.Count,
                FirstOccurred = members[0].Member.Occurred,
                LastOccurred = members[^1].Member.Occurred,
                DueAt = members.Min(h => h.Member.DueAt),
                Members = members.Take(pageSize).Select(h => h.Member).ToList(),
                ContinuingMemberCount = continuing.Count,
                ContinuingItemIds = continuing.Take(limit).ToList()
            });
        }

        foreach (var leafGroup in frontier
                     .Where(f => _hops.All(h => h.CauseItemId != f.RunProfileExecutionItemId))
                     .GroupBy(f => (f.ParentCohortOrdinal, Ending: _leafEndings.GetValueOrDefault(f.RunProfileExecutionItemId))))
        {
            var ending = leafGroup.Key.Ending;
            level.Leaves.Add(new CausalConsequenceLeafRow
            {
                ParentCohortOrdinal = leafGroup.Key.ParentCohortOrdinal,
                Ending = ending?.Ending ?? CausalConsequenceEnding.NoFurtherConsequences,
                ConnectedSystemId = ending?.ConnectedSystemId,
                ConnectedSystemName = ending?.ConnectedSystemName,
                MemberCount = leafGroup.Count()
            });
        }

        return level;
    }
}
