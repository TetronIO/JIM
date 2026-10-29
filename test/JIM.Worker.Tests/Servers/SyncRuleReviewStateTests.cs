// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What a Synchronisation Rule is waiting on (#2615): an import rule, for the next synchronisation of its Connected System
/// to process every object of its type; an export rule, for the Metaverse Objects of its type flagged for review to be
/// checked against every export rule. Read in batch for a list of rules, so a list does not cost a query per rule.
/// </summary>
[TestFixture]
public class SyncRuleReviewStateTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>())).ReturnsAsync([]);
        _metaverseRepo.Setup(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync([]);
        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task PopulateSyncRuleReviewStateAsync_ImportRuleWhoseTypeIsAwaiting_GetsTheNumberOfChangesWaitingForThatTypeAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(
            [
                Awaiting(systemId: 3, objectTypeId: 7, changeCount: 4),
                Awaiting(systemId: 3, objectTypeId: 8, changeCount: 1),
                Awaiting(systemId: 5, objectTypeId: 7, changeCount: 9)
            ]);
        var users = Import(id: 1, systemId: 3, objectTypeId: 7);
        var groups = Import(id: 2, systemId: 3, objectTypeId: 8);
        var otherSystem = Import(id: 3, systemId: 4, objectTypeId: 7);

        await _jim.ConnectedSystems.PopulateSyncRuleReviewStateAsync([users, groups, otherSystem]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(users.AwaitingSynchronisationChangeCount, Is.EqualTo(4), "the type's changes, not another system's of the same type id");
            Assert.That(groups.AwaitingSynchronisationChangeCount, Is.EqualTo(1));
            Assert.That(otherSystem.AwaitingSynchronisationChangeCount, Is.Zero, "nothing waits for that system");
        }
    }

    [Test]
    public async Task PopulateSyncRuleReviewStateAsync_ExportRule_GetsTheNumberOfMetaverseObjectsOfItsTypeFlaggedForReviewAsync()
    {
        _metaverseRepo.Setup(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, int> { [11] = 1_250 });
        var flagged = Export(id: 1, metaverseObjectTypeId: 11);
        var settled = Export(id: 2, metaverseObjectTypeId: 12);

        await _jim.ConnectedSystems.PopulateSyncRuleReviewStateAsync([flagged, settled]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flagged.ExportReviewPendingCount, Is.EqualTo(1_250));
            Assert.That(settled.ExportReviewPendingCount, Is.Zero);
        }
    }

    [Test]
    public async Task PopulateSyncRuleReviewStateAsync_EachKindOfRule_IsOnlyGivenWhatApplies()
    {
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([Awaiting(systemId: 3, objectTypeId: 7, changeCount: 4)]);
        _metaverseRepo.Setup(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, int> { [1] = 6 });
        var import = Import(id: 1, systemId: 3, objectTypeId: 7, metaverseObjectTypeId: 1);
        var export = Export(id: 2, metaverseObjectTypeId: 1, systemId: 3, objectTypeId: 7);

        await _jim.ConnectedSystems.PopulateSyncRuleReviewStateAsync([import, export]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(import.ExportReviewPendingCount, Is.Zero, "an import rule has no export review");
            Assert.That(export.AwaitingSynchronisationChangeCount, Is.Zero, "an export rule is not what a synchronisation of its type waits on");
            Assert.That(import.AwaitingSynchronisationChangeCount, Is.EqualTo(4));
            Assert.That(export.ExportReviewPendingCount, Is.EqualTo(6));
        }
    }

    [Test]
    public async Task PopulateSyncRuleReviewStateAsync_ManyRules_ReadsEachKindOnceAsync()
    {
        // A list of rules must not cost a read per rule.
        var rules = Enumerable.Range(1, 20).Select(i => i % 2 == 0 ? Import(i, systemId: i, objectTypeId: i) : Export(i, metaverseObjectTypeId: i)).ToList();

        await _jim.ConnectedSystems.PopulateSyncRuleReviewStateAsync(rules);

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepo.Verify(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()), Times.Once);
            _metaverseRepo.Verify(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Once);
        }
    }

    [Test]
    public async Task PopulateSyncRuleReviewStateAsync_OnlyImportRules_DoesNotReadExportReviewsAsync()
    {
        await _jim.ConnectedSystems.PopulateSyncRuleReviewStateAsync([Import(1, systemId: 3, objectTypeId: 7)]);

        _metaverseRepo.Verify(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Never);
    }

    [Test]
    public async Task PopulateSyncRuleReviewStateAsync_OnlyExportRules_DoesNotReadChangesAwaitingSynchronisationAsync()
    {
        await _jim.ConnectedSystems.PopulateSyncRuleReviewStateAsync([Export(1, metaverseObjectTypeId: 11)]);

        _connectedSystemRepo.Verify(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()), Times.Never);
    }

    [Test]
    public async Task PopulateSyncRuleReviewStateAsync_NoRules_ReadsNothingAsync()
    {
        await _jim.ConnectedSystems.PopulateSyncRuleReviewStateAsync([]);

        _connectedSystemRepo.Verify(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()), Times.Never);
        _metaverseRepo.Verify(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Never);
    }

    [Test]
    public async Task GetConfigurationChangeSynchronisationsForActivityAsync_AsksForTheListedMaximumAsync()
    {
        var activityId = Guid.NewGuid();
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(activityId, ObjectTypeAwaitingSynchronisation.MaxChangesListed))
            .ReturnsAsync([new ConfigurationChangeSynchronisationSummary { Id = 1, Reason = "HR Import: Scope changed" }]);

        var changes = await _jim.ConnectedSystems.GetConfigurationChangeSynchronisationsForActivityAsync(activityId);

        Assert.That(changes.Select(c => c.Reason), Is.EqualTo(new[] { "HR Import: Scope changed" }));
    }

    [Test]
    public async Task GetExportReviewPendingCountAsync_TypeWithFlaggedObjects_ReturnsHowManyAsync()
    {
        _metaverseRepo.Setup(r => r.GetExportReviewPendingCountsAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 11 }))))
            .ReturnsAsync(new Dictionary<int, int> { [11] = 40 });

        Assert.That(await _jim.Metaverse.GetExportReviewPendingCountAsync(11), Is.EqualTo(40));
    }

    [Test]
    public async Task GetExportReviewPendingCountAsync_TypeWithNothingFlagged_ReturnsZeroAsync()
    {
        Assert.That(await _jim.Metaverse.GetExportReviewPendingCountAsync(11), Is.Zero);
    }

    private static ObjectTypeAwaitingSynchronisation Awaiting(int systemId, int objectTypeId, int changeCount) => new()
    {
        ConnectedSystemId = systemId,
        ConnectedSystemObjectTypeId = objectTypeId,
        ObjectTypeName = $"type {objectTypeId}",
        ChangeCount = changeCount,
        EarliestRequestedAt = DateTime.UtcNow.AddHours(-1)
    };

    private static SyncRuleHeader Import(int id, int systemId, int objectTypeId, int metaverseObjectTypeId = 1) => new()
    {
        Id = id,
        Name = $"import {id}",
        Direction = SyncRuleDirection.Import,
        ConnectedSystemId = systemId,
        ConnectedSystemObjectTypeId = objectTypeId,
        MetaverseObjectTypeId = metaverseObjectTypeId
    };

    private static SyncRuleHeader Export(int id, int metaverseObjectTypeId, int systemId = 3, int objectTypeId = 7) => new()
    {
        Id = id,
        Name = $"export {id}",
        Direction = SyncRuleDirection.Export,
        ConnectedSystemId = systemId,
        ConnectedSystemObjectTypeId = objectTypeId,
        MetaverseObjectTypeId = metaverseObjectTypeId
    };
}
