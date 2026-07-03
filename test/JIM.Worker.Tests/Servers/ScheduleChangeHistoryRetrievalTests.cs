// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Scheduling;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for the Guid-keyed configuration change-history read path on
/// <see cref="JIM.Application.Servers.ChangeHistoryServer"/>, used by Guid-keyed configuration objects (Schedules):
/// the paged summary list, the single-change detail (snapshot plus diff against the predecessor), and the
/// compare-two-versions diff. Mirrors the int-keyed retrieval tests so both key shapes stay behaviourally identical.
/// </summary>
[TestFixture]
public class ScheduleChangeHistoryRetrievalTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _jim = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid InitiatorId = Guid.NewGuid();
    private static readonly Guid ScheduleId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task GetConfigurationChangeHistoryAsync_GuidKeyed_ReturnsPagedItemsWithPerRowSummariesAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(version: 2, ActivityTargetOperationType.Update, SnapJson(Sched("v2"))),
            Data(version: 1, ActivityTargetOperationType.Create, SnapJson(Sched("v1")))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.Schedule, ScheduleId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.Schedule, ScheduleId, 0, 21)).ReturnsAsync(rows);

        var result = await _jim.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.Schedule, ScheduleId);

        Assert.That(result.TotalResults, Is.EqualTo(2));
        Assert.That(result.Results, Has.Count.EqualTo(2));
        Assert.That(result.Results[0].Version, Is.EqualTo(2));
        Assert.That(result.Results[0].Summary, Is.EqualTo("Description"), "v2 changed the description versus v1");
        Assert.That(result.Results[0].Diff, Is.Not.Null, "v2 must carry its diff against v1 for inline rendering");
        Assert.That(result.Results[0].Diff!.ObjectGuidId, Is.EqualTo(ScheduleId), "the diff must carry the Guid object id");
        Assert.That(result.Results[1].Version, Is.EqualTo(1));
        Assert.That(result.Results[1].Summary, Is.EqualTo("Created"), "v1 is the creation");
        Assert.That(result.Results[1].Operation, Is.EqualTo(ActivityTargetOperationType.Create));
    }

    [Test]
    public async Task GetConfigurationChangeAsync_GuidKeyed_ReturnsSnapshotAndDiffAgainstPredecessorAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Schedule, ScheduleId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, SnapJson(Sched("v2"))));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.Schedule, ScheduleId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, SnapJson(Sched("v1"))));

        var detail = await _jim.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.Schedule, ScheduleId, 2);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("Nightly Sync"));
        Assert.That(detail.Snapshot.ObjectGuidId, Is.EqualTo(ScheduleId));
        Assert.That(detail.Diff.OldVersion, Is.EqualTo(1));
        Assert.That(detail.Diff.NewVersion, Is.EqualTo(2));
        Assert.That(detail.Diff.ModifiedCount, Is.EqualTo(1), "the description changed between v1 and v2");
    }

    [Test]
    public async Task GetConfigurationChangeAsync_GuidKeyed_ReturnsNullWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var detail = await _jim.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.Schedule, ScheduleId, 99);

        Assert.That(detail, Is.Null);
    }

    [Test]
    public async Task CompareConfigurationChangesAsync_GuidKeyed_DiffsTwoArbitraryVersionsAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Schedule, ScheduleId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, SnapJson(Sched("a"))));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.Schedule, ScheduleId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, SnapJson(Sched("c"))));

        var diff = await _jim.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.Schedule, ScheduleId, 1, 3);

        Assert.That(diff, Is.Not.Null);
        Assert.That(diff!.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.EqualTo(1));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private string SnapJson(Schedule schedule) => ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(schedule, HashKey));

    private static Schedule Sched(string description) => new()
    {
        Id = ScheduleId,
        Name = "Nightly Sync",
        Description = description,
        TriggerType = ScheduleTriggerType.Manual,
        IsEnabled = true
    };

    private static ConfigurationChangeActivityData Data(int version, ActivityTargetOperationType operation, string snapshotJson) => new()
    {
        ActivityId = Guid.NewGuid(),
        Version = version,
        Operation = operation,
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedById = InitiatorId,
        InitiatedByName = "Tester",
        When = When,
        Reason = null,
        SnapshotJson = snapshotJson
    };
}
