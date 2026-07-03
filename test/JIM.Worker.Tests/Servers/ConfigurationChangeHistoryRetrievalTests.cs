// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for configuration change-history retrieval on <see cref="JIM.Application.Servers.ChangeHistoryServer"/>: the
/// paged summary list (with per-row change summaries), the single-change detail (snapshot plus diff against the
/// predecessor), and the compare-two-versions diff.
/// </summary>
[TestFixture]
public class ConfigurationChangeHistoryRetrievalTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _jim = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid InitiatorId = Guid.NewGuid();

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
    public async Task GetConfigurationChangeHistoryAsync_ReturnsPagedItemsWithPerRowSummariesAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(version: 2, ActivityTargetOperationType.Update, SnapJson(Cs("v2"))),
            Data(version: 1, ActivityTargetOperationType.Create, SnapJson(Cs("v1")))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ConnectedSystem, 9)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ConnectedSystem, 9, 0, 21)).ReturnsAsync(rows);

        var result = await _jim.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ConnectedSystem, 9);

        Assert.That(result.TotalResults, Is.EqualTo(2));
        Assert.That(result.CurrentPage, Is.EqualTo(1));
        Assert.That(result.Results, Has.Count.EqualTo(2));
        Assert.That(result.Results[0].Version, Is.EqualTo(2));
        Assert.That(result.Results[0].Summary, Is.EqualTo("Description"), "v2 changed the description versus v1");
        Assert.That(result.Results[1].Version, Is.EqualTo(1));
        Assert.That(result.Results[1].Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetConfigurationChangeHistoryAsync_ReportsObjectLevelOperationAndInitiatorIdAsync()
    {
        // v2 was recorded by a granular sub-entity endpoint whose activity carries operation Create (e.g. adding an
        // Attribute Flow mapping), but at the object level it is an update of an already-existing object. Only v1, which
        // has no predecessor, is a genuine creation.
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(version: 2, ActivityTargetOperationType.Create, SnapJson(Cs("v2"))),
            Data(version: 1, ActivityTargetOperationType.Create, SnapJson(Cs("v1")))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ConnectedSystem, 9)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ConnectedSystem, 9, 0, 21)).ReturnsAsync(rows);

        var result = await _jim.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ConnectedSystem, 9);

        Assert.That(result.Results[0].Version, Is.EqualTo(2));
        Assert.That(result.Results[0].Operation, Is.EqualTo(ActivityTargetOperationType.Update),
            "a later version is an object-level update even when its recording activity was a sub-entity create");
        Assert.That(result.Results[1].Version, Is.EqualTo(1));
        Assert.That(result.Results[1].Operation, Is.EqualTo(ActivityTargetOperationType.Create), "the first version is the creation");
        Assert.That(result.Results[0].InitiatedById, Is.EqualTo(InitiatorId), "the principal id must flow through so the UI can link to it");
    }

    [Test]
    public async Task GetConfigurationChangeHistoryAsync_AttachesDiffToEachItemAsync()
    {
        // The list carries the full diff per row so the UI renders it inline without a second round-trip; the diff is
        // already computed to build the summary, so attaching it is free.
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(version: 2, ActivityTargetOperationType.Update, SnapJson(Cs("v2"))),
            Data(version: 1, ActivityTargetOperationType.Create, SnapJson(Cs("v1")))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.ConnectedSystem, 9)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.ConnectedSystem, 9, 0, 21)).ReturnsAsync(rows);

        var result = await _jim.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ConnectedSystem, 9);

        Assert.That(result.Results[0].Diff, Is.Not.Null, "v2 must carry its diff against v1");
        Assert.That(result.Results[0].Diff!.ModifiedCount, Is.EqualTo(1), "the description changed between v1 and v2");
        Assert.That(result.Results[0].Diff.OldVersion, Is.EqualTo(1));
        Assert.That(result.Results[0].Diff.NewVersion, Is.EqualTo(2));

        // The first version has no predecessor, so its diff shows the whole object as created (root Added).
        Assert.That(result.Results[1].Diff, Is.Not.Null, "the creation row must carry a diff too");
        Assert.That(result.Results[1].Diff!.Root.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Added));
    }

    [Test]
    public async Task GetConfigurationChangeAsync_ReturnsSnapshotAndDiffAgainstPredecessorAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ConnectedSystem, 9, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, SnapJson(Cs("v2"))));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.ConnectedSystem, 9, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, SnapJson(Cs("v1"))));

        var detail = await _jim.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.ConnectedSystem, 9, 2);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("AD"));
        Assert.That(detail.Diff.OldVersion, Is.EqualTo(1));
        Assert.That(detail.Diff.NewVersion, Is.EqualTo(2));
        Assert.That(detail.Diff.ModifiedCount, Is.EqualTo(1), "the description changed between v1 and v2");
    }

    [Test]
    public async Task GetConfigurationChangeAsync_ReturnsNullWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var detail = await _jim.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.ConnectedSystem, 9, 99);

        Assert.That(detail, Is.Null);
    }

    [Test]
    public async Task CompareConfigurationChangesAsync_DiffsTwoArbitraryVersionsAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ConnectedSystem, 9, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, SnapJson(Cs("a"))));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.ConnectedSystem, 9, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, SnapJson(Cs("c"))));

        var diff = await _jim.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.ConnectedSystem, 9, 1, 3);

        Assert.That(diff, Is.Not.Null);
        Assert.That(diff!.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.EqualTo(1));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private string SnapJson(ConnectedSystem cs) => ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(cs, HashKey));

    private static ConnectedSystem Cs(string description) => new() { Id = 9, Name = "AD", ConnectorDefinitionId = 4, Description = description };

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
