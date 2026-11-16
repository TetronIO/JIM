// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging;
using Moq;

namespace JIM.Worker.Tests;

/// <summary>
/// #2521 Phase 4: while a Connected System is marked import incomplete (<see cref="ConnectedSystem.ImportIncompleteSince"/>),
/// its Connector Space holds part of an import that stopped, and Full and Delta Synchronisation refuse to run against it:
/// one would remove every Metaverse reference whose target the import had not yet saved, and export the removals. The
/// refusal fails the run's Activity with the reason and the remedy, never silently, as a disabled Run Profile's does
/// (<see cref="WorkerDisabledRunProfileTests"/>), so a Schedule that runs the synchronisation records the step as failed.
/// Imports run (they are the remedy), and so do exports, which send what earlier synchronisations decided.
/// </summary>
[TestFixture]
public class WorkerImportIncompleteRefusalTests
{
    private static readonly DateTime StoppedAt = new(2026, 11, 16, 10, 3, 12, DateTimeKind.Utc);

    private Mock<IActivityRepository> _activityRepository = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _activityRepository = new Mock<IActivityRepository>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _jim = new JimApplication(repository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public async Task RefuseIfImportIncompleteAsync_Synchronisation_FailsTheActivitySayingWhyAndWhatToDoAsync(ConnectedSystemRunType runType)
    {
        var activity = new Activity { Status = ActivityStatus.InProgress, Executed = DateTime.UtcNow };
        var connectedSystem = new ConnectedSystem { Id = 4, Name = "HR System", ImportIncompleteSince = StoppedAt };
        var runProfile = new ConnectedSystemRunProfile { Id = 10, Name = "Synchronise HR", RunType = runType };

        var refused = await Worker.RefuseIfImportIncompleteAsync(_jim, activity, connectedSystem, runProfile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.True, "the run must not go ahead");
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError), "a refused run fails its Activity, never silently");
            Assert.That(activity.ErrorMessage, Is.EqualTo(ImportCompleteness.DescribeSynchronisationRefusal(connectedSystem, runProfile)));
            Assert.That(activity.ErrorMessage, Does.Contain("'HR System'"), "it names the Connected System");
            Assert.That(activity.ErrorMessage, Does.Contain("16 Nov 2026 10:03:12 UTC"), "it names the stopped import by when it began writing");
            Assert.That(activity.ErrorMessage, Does.Contain("Run a Full Import"), "it gives the remedy");
        }

        _activityRepository.Verify(r => r.UpdateActivityAsync(activity), Times.Once, "the failure is recorded");
    }

    [TestCase(ConnectedSystemRunType.FullImport)]
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.Export)]
    public async Task RefuseIfImportIncompleteAsync_ImportOrExport_LetsTheRunGoAheadAsync(ConnectedSystemRunType runType)
    {
        var activity = new Activity { Status = ActivityStatus.InProgress };
        var connectedSystem = new ConnectedSystem { Id = 4, Name = "HR System", ImportIncompleteSince = StoppedAt };
        var runProfile = new ConnectedSystemRunProfile { Id = 10, Name = "Run", RunType = runType };

        var refused = await Worker.RefuseIfImportIncompleteAsync(_jim, activity, connectedSystem, runProfile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False, "an import is the remedy, and an export sends what earlier synchronisations decided");
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.InProgress));
        }

        _activityRepository.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public async Task RefuseIfImportIncompleteAsync_NotMarked_LetsTheRunGoAheadAsync(ConnectedSystemRunType runType)
    {
        var activity = new Activity { Status = ActivityStatus.InProgress };
        var connectedSystem = new ConnectedSystem { Id = 4, Name = "HR System" };
        var runProfile = new ConnectedSystemRunProfile { Id = 10, Name = "Synchronise HR", RunType = runType };

        var refused = await Worker.RefuseIfImportIncompleteAsync(_jim, activity, connectedSystem, runProfile);

        Assert.That(refused, Is.False);
        _activityRepository.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public void DescribeSynchronisationRefusal_ConnectedSystemWithSelectedPartitions_AsksForAFullImportOfEveryOne()
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = 4,
            Name = "Directory",
            ImportIncompleteSince = StoppedAt,
            Partitions = [new ConnectedSystemPartition { Name = "corp.local", Selected = true }]
        };
        var runProfile = new ConnectedSystemRunProfile { Id = 10, Name = "Synchronise Directory", RunType = ConnectedSystemRunType.DeltaSynchronisation };

        var message = ImportCompleteness.DescribeSynchronisationRefusal(connectedSystem, runProfile);

        Assert.That(message, Does.Contain("all selected partitions"),
            "a Full Import scoped to one partition does not clear the marker, so the remedy must say so");
    }
}
