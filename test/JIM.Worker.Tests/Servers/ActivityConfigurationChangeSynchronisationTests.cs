// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What an Activity says about the configuration changes a synchronisation applies (#2615): a configuration change
/// Activity, which of its changes have been applied; a synchronisation run, which changes it claimed and how many objects
/// of each type it processed because of them. Read as one group per Connected System Object Type.
/// </summary>
[TestFixture]
public class ActivityConfigurationChangeSynchronisationTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private static Activity ActivityOf(Guid id, ActivityTargetType targetType = ActivityTargetType.SynchronisationRule) =>
        new() { Id = id, TargetType = targetType };

    private void Returns(Guid activityId, params ConfigurationChangeSynchronisationSummary[] changes) =>
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(activityId, ObjectTypeAwaitingSynchronisation.MaxChangesListed))
            .ReturnsAsync(changes.ToList());

    [Test]
    public async Task GetConfigurationChangeSynchronisationsForActivityAsync_NothingRecorded_ReturnsNoGroupsAsync()
    {
        var activityId = Guid.NewGuid();
        Returns(activityId);

        Assert.That(await _jim.ConnectedSystems.GetConfigurationChangeSynchronisationsForActivityAsync(ActivityOf(activityId)), Is.Empty);
    }

    [TestCase(ActivityTargetType.PasswordSynchronisation)]
    [TestCase(ActivityTargetType.HistoryRetentionCleanup)]
    [TestCase(ActivityTargetType.MetaverseObject)]
    [TestCase(ActivityTargetType.Authentication)]
    public async Task GetConfigurationChangeSynchronisationsForActivityAsync_ActivityThatCanHaveNone_ReadsNothingAsync(ActivityTargetType targetType)
    {
        var groups = await _jim.ConnectedSystems.GetConfigurationChangeSynchronisationsForActivityAsync(ActivityOf(Guid.NewGuid(), targetType));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups, Is.Empty);
            _connectedSystemRepo.Verify(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        }
    }

    [Test]
    public async Task GetConfigurationChangeSynchronisationsForActivityAsync_ChangesToTwoTypes_GroupsThemByTypeAsync()
    {
        var activityId = Guid.NewGuid();
        Returns(activityId,
            Change(1, systemId: 3, typeId: 7, typeName: "contact", reason: "a"),
            Change(2, systemId: 3, typeId: 8, typeName: "user", reason: "b"),
            Change(3, systemId: 3, typeId: 8, typeName: "user", reason: "c"));

        var groups = await _jim.ConnectedSystems.GetConfigurationChangeSynchronisationsForActivityAsync(ActivityOf(activityId));

        Assert.That(groups.Select(g => (g.ConnectedSystemObjectTypeId, g.ObjectTypeName, Reasons: string.Join(",", g.Changes.Select(c => c.Reason)))),
            Is.EqualTo(new[] { (7, "contact", "a"), (8, "user", "b,c") }));
    }

    [Test]
    public async Task GetConfigurationChangeSynchronisationsForActivityAsync_ConfigurationChangeActivity_DoesNotCountObjectsAsync()
    {
        // A configuration change Activity recorded the change; a run claims it. Counting a type's objects is the run's.
        var activityId = Guid.NewGuid();
        Returns(activityId, Change(1, systemId: 3, typeId: 7, typeName: "user", claimedBy: Guid.NewGuid()));

        var groups = await _jim.ConnectedSystems.GetConfigurationChangeSynchronisationsForActivityAsync(ActivityOf(activityId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups.Single().ObjectCount, Is.Null);
            _connectedSystemRepo.Verify(r => r.GetConnectedSystemObjectCountAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<int?>()), Times.Never);
        }
    }

    [Test]
    public async Task GetConfigurationChangeSynchronisationsForActivityAsync_SynchronisationRun_CountsTheObjectsOfEachTypeItClaimedAsync()
    {
        var runId = Guid.NewGuid();
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(3, 7, null)).ReturnsAsync(12_500);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(3, 8, null)).ReturnsAsync(40);
        Returns(runId,
            Change(1, systemId: 3, typeId: 7, typeName: "user", claimedBy: runId),
            Change(2, systemId: 3, typeId: 7, typeName: "user", claimedBy: runId),
            Change(3, systemId: 3, typeId: 8, typeName: "group", claimedBy: runId));

        var groups = await _jim.ConnectedSystems.GetConfigurationChangeSynchronisationsForActivityAsync(ActivityOf(runId, ActivityTargetType.ConnectedSystemRunProfile));

        Assert.That(groups.Select(g => (g.ObjectTypeName, g.ObjectCount)), Is.EqualTo(new[] { ("user", (int?)12_500), ("group", 40) }),
            "one count per type, however many changes the run claimed for it");
        _connectedSystemRepo.Verify(r => r.GetConnectedSystemObjectCountAsync(3, 7, null), Times.Once);
    }

    [Test]
    public void IsApplied_EveryChangeApplied_IsTrueWithTheLastAppliedTimeAndTheRunThatAppliedIt()
    {
        var run = Guid.NewGuid();
        var earlier = DateTime.UtcNow.AddMinutes(-10);
        var later = DateTime.UtcNow.AddMinutes(-5);
        var group = new ActivityObjectTypeSynchronisation
        {
            Changes =
            [
                Change(1, systemId: 3, typeId: 7, typeName: "user", claimedBy: Guid.NewGuid(), appliedAt: earlier),
                Change(2, systemId: 3, typeId: 7, typeName: "user", claimedBy: run, appliedAt: later)
            ]
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.IsApplied, Is.True);
            Assert.That(group.AppliedAt, Is.EqualTo(later));
            Assert.That(group.AppliedByActivityId, Is.EqualTo(run));
        }
    }

    [Test]
    public void IsApplied_AnyChangeStillAwaiting_IsFalseWithNoAppliedDetail()
    {
        var group = new ActivityObjectTypeSynchronisation
        {
            Changes =
            [
                Change(1, systemId: 3, typeId: 7, typeName: "user", claimedBy: Guid.NewGuid(), appliedAt: DateTime.UtcNow),
                Change(2, systemId: 3, typeId: 7, typeName: "user")
            ]
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.IsApplied, Is.False);
            Assert.That(group.AppliedAt, Is.Null);
            Assert.That(group.AppliedByActivityId, Is.Null);
        }
    }

    [Test]
    public void IsApplied_NoChanges_IsFalse()
    {
        Assert.That(new ActivityObjectTypeSynchronisation().IsApplied, Is.False);
    }

    private static ConfigurationChangeSynchronisationSummary Change(
        long id, int systemId, int typeId, string typeName, string reason = "change", Guid? claimedBy = null, DateTime? appliedAt = null) => new()
    {
        Id = id,
        ConnectedSystemId = systemId,
        ConnectedSystemName = $"system {systemId}",
        ConnectedSystemObjectTypeId = typeId,
        ObjectTypeName = typeName,
        Reason = reason,
        RequestedAt = DateTime.UtcNow.AddHours(-1),
        ClaimedByActivityId = claimedBy,
        AppliedAt = appliedAt
    };
}
